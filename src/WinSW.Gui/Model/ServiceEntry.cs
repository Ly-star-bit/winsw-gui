using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Localization;

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

        private string wrapperVersion = string.Empty;
        private ServiceControllerStatus? status;
        private int processId;
        private string? problem;
        private int? lastExitCode;
        private DateTime? startedAt;
        private double cpuPercent;
        private long workingSetBytes;
        private int handleCount;
        private TimeSpan lastCpuTime;
        private DateTime lastSampleAt;
        private readonly List<double> cpuHistory = new();
        private int crashCount;

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

        public string Description { get; init; } = string.Empty;

        public string StartMode { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;

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
        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        /// <summary>Services that will be stopped if this one stops.</summary>
        public IReadOnlyList<string> DependedBy { get; init; } = Array.Empty<string>();

        public string DependsOnText => this.DependsOn.Count == 0 ? "—" : string.Join(", ", this.DependsOn);

        public string DependedByText => this.DependedBy.Count == 0 ? "—" : string.Join(", ", this.DependedBy);

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
                }
            }
        }

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
