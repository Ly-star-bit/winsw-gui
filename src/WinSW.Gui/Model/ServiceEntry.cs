using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;

namespace WinSW.Gui.Model
{
    public enum ServiceHealth
    {
        Unknown,
        Running,
        Stopped,
        Pending,
        Broken,
    }

    /// <summary>
    /// One installed Windows service that is hosted by a WinSW wrapper executable.
    /// </summary>
    public sealed class ServiceEntry : ObservableObject
    {
        private const int HistoryLength = 40;

        /// <summary>
        /// Memory is traced slowly and for longer than CPU. A leak is a climb over an hour, not
        /// over the eighty seconds the CPU trace covers at the poll rate, where it is noise.
        /// </summary>
        private const int MemoryHistoryLength = 60;

        private static readonly TimeSpan MemoryHistoryStep = TimeSpan.FromMinutes(1);

        private string wrapperVersion = string.Empty;
        private string description = string.Empty;
        private string startMode = string.Empty;
        private string account = string.Empty;
        private IReadOnlyList<string> dependsOn = Array.Empty<string>();
        private IReadOnlyList<string> dependedBy = Array.Empty<string>();
        private ServiceControllerStatus? status;
        private int processId;
        private string? problem;
        private int? lastExitCode;
        private DateTime? startedAt;
        private DateTime? configWrittenAt;
        private double cpuPercent;
        private long workingSetBytes;
        private int handleCount;
        private TimeSpan lastCpuTime;
        private DateTime lastSampleAt;
        private readonly List<double> cpuHistory = new();
        private readonly List<double> memoryHistory = new();
        private DateTime lastMemoryPointAt;
        private int memoryProcessId;
        private int crashCount;
        private string group = string.Empty;

        public ServiceEntry(string serviceName, string displayName, string wrapperPath, string? configPath)
        {
            this.ServiceName = serviceName;
            this.DisplayName = displayName;
            this.WrapperPath = wrapperPath;
            this.ConfigPath = configPath;
        }

        public string ServiceName { get; }

        public string DisplayName { get; }

        /// <summary>The WinSW executable registered as the service image.</summary>
        public string WrapperPath { get; }

        /// <summary>
        /// The configuration the wrapper was installed with, resolved either from the
        /// service's command line or from the executable's own name.
        /// </summary>
        public string? ConfigPath { get; }

        // Settable rather than init-only: a background rescan finds an entry that already
        // exists and has to be able to bring these forward. Another tool — services.msc, sc
        // config, a reinstall — can change any of them under a running console.
        public string Description
        {
            get => this.description;
            set => this.Set(ref this.description, value);
        }

        public string StartMode
        {
            get => this.startMode;
            set => this.Set(ref this.startMode, value);
        }

        public string Account
        {
            get => this.account;
            set => this.Set(ref this.account, value);
        }

        /// <summary>
        /// File version of the wrapper executable, e.g. 3.0.0.96. Settable rather than
        /// init-only because an upgrade replaces that file under a live entry, and the
        /// detail panel has to stop showing the version that is no longer on disk.
        /// </summary>
        public string WrapperVersion
        {
            get => this.wrapperVersion;
            set => this.Set(ref this.wrapperVersion, value);
        }

        /// <summary>Services that must be running before this one starts.</summary>
        /// <remarks>
        /// Compared by content, not by reference. Every rescan hands over a fresh array, and
        /// the default comparer would call each one a change and repaint the detail panel
        /// twice a minute for nothing.
        /// </remarks>
        public IReadOnlyList<string> DependsOn
        {
            get => this.dependsOn;
            set
            {
                if (!Same(this.dependsOn, value))
                {
                    this.dependsOn = value;
                    this.Raise();
                    this.Raise(nameof(this.DependsOnText));
                }
            }
        }

        /// <summary>Services that will be stopped if this one stops. Compared by content.</summary>
        public IReadOnlyList<string> DependedBy
        {
            get => this.dependedBy;
            set
            {
                if (!Same(this.dependedBy, value))
                {
                    this.dependedBy = value;
                    this.Raise();
                    this.Raise(nameof(this.DependedByText));
                }
            }
        }

        private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public string DependsOnText => this.DependsOn.Count == 0 ? "—" : string.Join(", ", this.DependsOn);

        public string DependedByText => this.DependedBy.Count == 0 ? "—" : string.Join(", ", this.DependedBy);

        /// <summary>
        /// True when <paramref name="other"/> describes the same service well enough that its
        /// values can be merged into this entry instead of replacing it.
        /// </summary>
        /// <remarks>
        /// The name is the identity as far as the service control manager is concerned, but a
        /// service pointed at a different executable or a different configuration file is a
        /// different thing wearing the same name, and its history — the CPU trace, the crash
        /// count — no longer describes what is running now.
        /// </remarks>
        public bool IsSameInstallationAs(ServiceEntry other) =>
            string.Equals(this.WrapperPath, other.WrapperPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(this.ConfigPath, other.ConfigPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Brings forward everything a rescan can see that the status poll does not.
        /// </summary>
        /// <remarks>
        /// The rescan merges into the existing entries rather than replacing them, so that a
        /// selection, a scroll position and the per-row health history survive it. Without
        /// this the merge kept the stale values too: a start mode or a service account changed
        /// by services.msc did not appear until the console was restarted.
        /// <para>
        /// Only what <see cref="Services.ServiceDiscovery.Discover"/> fills in belongs here.
        /// The live metrics are the status poll's, and copying them from a freshly discovered
        /// entry — which has none — would blank them twice a minute.
        /// </para>
        /// </remarks>
        public void MergeMetadataFrom(ServiceEntry other)
        {
            this.Description = other.Description;
            this.StartMode = other.StartMode;
            this.Account = other.Account;
            this.WrapperVersion = other.WrapperVersion;
            this.ConfigWrittenAt = other.ConfigWrittenAt;
            this.DependsOn = other.DependsOn;
            this.DependedBy = other.DependedBy;
            this.Problem = other.Problem;
        }

        // Live metrics --------------------------------------------------------

        /// <summary>The Win32 or service-specific exit code from the last stop, if any.</summary>
        public int? LastExitCode
        {
            get => this.lastExitCode;
            set
            {
                if (this.Set(ref this.lastExitCode, value))
                {
                    this.Raise(nameof(this.LastExitCodeText));
                }
            }
        }

        public string LastExitCodeText => this.lastExitCode is int code && code != 0 ? code.ToString() : "0";

        public DateTime? StartedAt
        {
            get => this.startedAt;
            set
            {
                if (this.Set(ref this.startedAt, value))
                {
                    this.Raise(nameof(this.UptimeText));
                    this.Raise(nameof(this.ConfigChangedSinceStart));
                }
            }
        }

        /// <summary>
        /// When the configuration file was last written, local time. Brought forward by each
        /// rescan, and set at once by the dashboard when the editor saves the file.
        /// </summary>
        public DateTime? ConfigWrittenAt
        {
            get => this.configWrittenAt;
            set
            {
                if (this.Set(ref this.configWrittenAt, value))
                {
                    this.Raise(nameof(this.ConfigChangedSinceStart));
                }
            }
        }

        /// <summary>
        /// The configuration was written after the running process started, so what is running
        /// is not what the file says. The wrapper reads its configuration once, at start, and
        /// 'winsw refresh' pushes only what the service control manager holds — the executable,
        /// its arguments, its environment and its logging all wait for a restart.
        /// </summary>
        public bool ConfigChangedSinceStart =>
            this.configWrittenAt is DateTime written && this.startedAt is DateTime started && written > started;

        public string UptimeText
        {
            get
            {
                if (this.startedAt is not DateTime started)
                {
                    return "—";
                }

                var span = DateTime.Now - started;
                return span.TotalDays >= 1
                    ? Localizer.Format("M.Metric.UptimeDays", (int)span.TotalDays, span.Hours, span.Minutes)
                    : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
            }
        }

        public double CpuPercent
        {
            get => this.cpuPercent;
            private set
            {
                if (this.Set(ref this.cpuPercent, value))
                {
                    this.Raise(nameof(this.CpuText));
                }
            }
        }

        public string CpuText => this.processId > 0 ? $"{this.cpuPercent:0.0}%" : "—";

        public long WorkingSetBytes
        {
            get => this.workingSetBytes;
            private set
            {
                if (this.Set(ref this.workingSetBytes, value))
                {
                    this.Raise(nameof(this.MemoryText));
                }
            }
        }

        public string MemoryText => this.processId > 0 ? $"{this.workingSetBytes / (1024.0 * 1024.0):0.#} MB" : "—";

        public int HandleCount
        {
            get => this.handleCount;
            private set
            {
                if (this.Set(ref this.handleCount, value))
                {
                    this.Raise(nameof(this.HandleText));
                }
            }
        }

        public string HandleText => this.processId > 0 ? this.handleCount.ToString() : "—";

        /// <summary>Recent CPU samples, oldest first, for the sparkline.</summary>
        public IReadOnlyList<double> CpuHistory => this.cpuHistory;

        /// <summary>Working set in megabytes, one point a minute for the last hour, oldest first.</summary>
        public IReadOnlyList<double> MemoryHistory => this.memoryHistory;

        /// <summary>
        /// Feeds one process sample. CPU is the processor time consumed since the previous
        /// sample, spread over the wall-clock interval and the machine's cores.
        /// </summary>
        public void Sample(TimeSpan totalProcessorTime, long workingSet, int handles, DateTime? started)
        {
            var now = DateTime.UtcNow;
            if (this.lastSampleAt != default && now > this.lastSampleAt)
            {
                double elapsed = (now - this.lastSampleAt).TotalMilliseconds;
                double used = (totalProcessorTime - this.lastCpuTime).TotalMilliseconds;
                double percent = Math.Clamp(used / elapsed / Environment.ProcessorCount * 100.0, 0, 100);
                this.CpuPercent = percent;

                this.cpuHistory.Add(percent);
                if (this.cpuHistory.Count > HistoryLength)
                {
                    this.cpuHistory.RemoveAt(0);
                }

                this.Raise(nameof(this.CpuHistory));
            }

            if (this.processId != this.memoryProcessId)
            {
                this.ResetMemoryHistory(this.processId);
            }

            if (now - this.lastMemoryPointAt >= MemoryHistoryStep)
            {
                this.lastMemoryPointAt = now;
                this.memoryHistory.Add(workingSet / (1024.0 * 1024.0));
                if (this.memoryHistory.Count > MemoryHistoryLength)
                {
                    this.memoryHistory.RemoveAt(0);
                }

                this.Raise(nameof(this.MemoryHistory));
            }

            this.lastSampleAt = now;
            this.lastCpuTime = totalProcessorTime;
            this.WorkingSetBytes = workingSet;
            this.HandleCount = handles;
            this.StartedAt = started;
            this.Raise(nameof(this.UptimeText));
        }

        public void ClearSample()
        {
            this.lastSampleAt = default;
            this.lastCpuTime = TimeSpan.Zero;
            this.CpuPercent = 0;
            this.WorkingSetBytes = 0;
            this.HandleCount = 0;
            this.StartedAt = null;
            if (this.cpuHistory.Count > 0)
            {
                this.cpuHistory.Clear();
                this.Raise(nameof(this.CpuHistory));
            }

            // Only a service that is not running loses its memory trace here. A running one
            // arrives in this method too, whenever a poll misses its process in the snapshot,
            // and an hour of trace is not worth dropping for one missed poll.
            if (this.processId == 0)
            {
                this.ResetMemoryHistory(0);
            }
        }

        /// <summary>
        /// Starts the memory trace over for <paramref name="processId"/>. A new process is a
        /// new trace: it starts from its own first minute, not from the end of a line that
        /// described the process before it.
        /// </summary>
        private void ResetMemoryHistory(int processId)
        {
            this.memoryProcessId = processId;
            this.lastMemoryPointAt = default;
            if (this.memoryHistory.Count > 0)
            {
                this.memoryHistory.Clear();
                this.Raise(nameof(this.MemoryHistory));
            }
        }

        public ServiceControllerStatus? Status
        {
            get => this.status;
            set
            {
                if (this.Set(ref this.status, value))
                {
                    this.Raise(nameof(this.Health));
                    this.Raise(nameof(this.SortRank));
                    this.Raise(nameof(this.StatusText));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.CanStop));
                }
            }
        }

        public int ProcessId
        {
            get => this.processId;
            set
            {
                if (this.Set(ref this.processId, value))
                {
                    this.Raise(nameof(this.ProcessIdText));
                    this.Raise(nameof(this.CpuText));
                    this.Raise(nameof(this.MemoryText));
                    this.Raise(nameof(this.HandleText));
                }
            }
        }

        public string ProcessIdText => this.processId > 0 ? $"PID {this.processId}" : "—";

        /// <summary>Set when the service is installed but its configuration is unusable.</summary>
        public string? Problem
        {
            get => this.problem;
            set
            {
                if (this.Set(ref this.problem, value))
                {
                    this.Raise(nameof(this.Health));
                    this.Raise(nameof(this.SortRank));
                    this.Raise(nameof(this.HasProblem));
                }
            }
        }

        public bool HasProblem => !string.IsNullOrEmpty(this.problem);

        /// <summary>
        /// The group the dashboard files this service under, or empty. Kept by the console per
        /// user, not by the service; see <see cref="Services.AppSettings.ServiceGroups"/>.
        /// </summary>
        public string Group
        {
            get => this.group;
            set
            {
                if (this.Set(ref this.group, value ?? string.Empty))
                {
                    this.Raise(nameof(this.GroupSortKey));
                }
            }
        }

        /// <summary>
        /// Groups in name order, with the ungrouped after all of them. A leading digit rather
        /// than a high character: the view sorts with the culture's collation, which may
        /// ignore a noncharacter altogether and put the ungrouped first.
        /// </summary>
        public string GroupSortKey => this.group.Length == 0 ? "1" : "0" + this.group;

        /// <summary>Unexpected stops seen in the current five-minute window; shown in the notification.</summary>
        public int CrashCount
        {
            get => this.crashCount;
            set => this.Set(ref this.crashCount, value);
        }

        /// <summary>Order for "sort by status": what needs a look first.</summary>
        public int SortRank => this.Health switch
        {
            ServiceHealth.Broken => 0,
            ServiceHealth.Pending => 1,
            ServiceHealth.Stopped => 2,
            ServiceHealth.Running => 3,
            _ => 4,
        };

        public ServiceHealth Health => this.problem != null ? ServiceHealth.Broken : this.status switch
        {
            ServiceControllerStatus.Running => ServiceHealth.Running,
            ServiceControllerStatus.Stopped => ServiceHealth.Stopped,
            null => ServiceHealth.Unknown,
            _ => ServiceHealth.Pending,
        };

        public string StatusText => Localizer.Get(this.status switch
        {
            ServiceControllerStatus.Running => "M.Status.Running",
            ServiceControllerStatus.Stopped => "M.Status.Stopped",
            ServiceControllerStatus.StartPending => "M.Status.Starting",
            ServiceControllerStatus.StopPending => "M.Status.Stopping",
            ServiceControllerStatus.PausePending => "M.Status.Pausing",
            ServiceControllerStatus.ContinuePending => "M.Status.Resuming",
            ServiceControllerStatus.Paused => "M.Status.Paused",
            _ => "M.Status.Unknown",
        });

        /// <summary>Re-evaluates the localized text after a language change.</summary>
        public void RefreshLocalized()
        {
            this.Raise(nameof(this.StatusText));
            this.Raise(nameof(this.UptimeText));
        }

        public bool CanStart => this.status == ServiceControllerStatus.Stopped;

        public bool CanStop => this.status == ServiceControllerStatus.Running
            || this.status == ServiceControllerStatus.Paused;

        /// <summary>The directory logs default to when the configuration does not override it.</summary>
        public string? DefaultLogDirectory =>
            this.ConfigPath is null ? null : Path.GetDirectoryName(this.ConfigPath);

        public override string ToString() => this.ServiceName;
    }
}
