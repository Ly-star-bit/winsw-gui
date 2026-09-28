using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    /// Why a service is counted as needing attention; see <see cref="ServiceEntry.Attention"/>.
    /// Several can hold at once, and are said in this order.
    /// </summary>
    [Flags]
    public enum AttentionReasons
    {
        None = 0,

        /// <summary>Its configuration is missing or cannot be used: <see cref="ServiceEntry.Problem"/>.</summary>
        ConfigProblem = 1,

        /// <summary>
        /// A program one of its runs left is still running, or something outside every wrapper
        /// holds its port: <see cref="ServiceEntry.StrayProcess"/>.
        /// </summary>
        StrayProcess = 2,

        /// <summary>Stopped by a failure, and Windows' recovery is about to start it again.</summary>
        RestartingByRecovery = 4,

        /// <summary>It has stopped lately without this console asking it to: <see cref="ServiceEntry.RecentStops"/>.</summary>
        RecentStops = 8,

        /// <summary>Stopped, and the service control manager holds a failure exit code for the stop.</summary>
        StoppedWithError = 16,

        /// <summary>Set to start automatically, and stopped.</summary>
        AutomaticButStopped = 32,
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

        /// <summary>How long a process has to stay behind before it is called stray; see <see cref="NoteStray"/>.</summary>
        private static readonly TimeSpan StrayConfirmation = TimeSpan.FromSeconds(3);

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
        private string? executablePath;
        private ImmutableArray<Services.ProcessMark> rememberedProcesses = ImmutableArray<Services.ProcessMark>.Empty;
        private Services.StrayFinding? strayProcess;
        private Services.StrayFinding? strayCandidate;
        private DateTime strayCandidateSince;
        private ImmutableArray<Services.ListeningPort> listeningPorts = ImmutableArray<Services.ListeningPort>.Empty;
        private ServiceStartMode? startType;
        private bool delayedAutoStart;
        private Services.RecoverySettings? recovery;
        private Services.RecoverySettings? declaredRecovery;
        private ServiceControllerStatus? recoverySeen;
        private DateTime? stoppedAt;
        private bool restartingByRecovery;
        private Services.LastStopReport? lastStop;
        private Services.LastStopText? lastStopText;
        private int stopStamp;
        private int lastStopReadFor = -1;
        private int recentStops;
        private ImmutableArray<EnvironmentFinding> startFindings = ImmutableArray<EnvironmentFinding>.Empty;
        private int runStamp;
        private DateTime? runningSince;
        private bool runHeld;

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

        /// <summary>
        /// The start type <see cref="StartMode"/> describes, for what depends on it rather than for
        /// showing it: a disabled service is not started by its recovery or by Start, and a start
        /// type is what "Stop restarting" changes and remembers. Null when the registry did not say.
        /// </summary>
        public ServiceStartMode? StartType
        {
            get => this.startType;
            set
            {
                if (this.Set(ref this.startType, value))
                {
                    this.Raise(nameof(this.CanStopRestarting));
                    this.Raise(nameof(this.IsStartable));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.StartUnavailableTip));
                    this.RaiseAttention();
                }
            }
        }

        /// <summary>An automatic start waits until the rest of the system has started; as the service control manager holds it.</summary>
        public bool DelayedAutoStart
        {
            get => this.delayedAutoStart;
            set => this.Set(ref this.delayedAutoStart, value);
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
            this.StartType = other.StartType;
            this.DelayedAutoStart = other.DelayedAutoStart;
            this.Recovery = other.Recovery;
            this.DeclaredRecovery = other.DeclaredRecovery;
            this.Account = other.Account;
            this.WrapperVersion = other.WrapperVersion;
            this.ConfigWrittenAt = other.ConfigWrittenAt;
            this.ExecutablePath = other.ExecutablePath;
            this.DependsOn = other.DependsOn;
            this.DependedBy = other.DependedBy;
            this.Problem = other.Problem;
        }

        // Recovery -------------------------------------------------------------

        /// <summary>
        /// What Windows does when the service fails: the Recovery tab of services.msc, as the service
        /// control manager holds it. Null when it could not be read. Brought forward by each rescan.
        /// </summary>
        public Services.RecoverySettings? Recovery
        {
            get => this.recovery;
            set
            {
                if (this.Set(ref this.recovery, value))
                {
                    this.Raise(nameof(this.RecoveryText));
                    this.Raise(nameof(this.RecoveryDiffers));
                    this.Raise(nameof(this.RecoveryDifferenceText));
                    this.Raise(nameof(this.CanStopRestarting));
                }
            }
        }

        /// <summary>
        /// What the configuration file's <c>&lt;onfailure&gt;</c> and <c>&lt;resetfailure&gt;</c> would
        /// have the wrapper set. Null when the file declares no failure actions: the wrapper then
        /// leaves the service's own as they are, so they cannot be said to differ from the file.
        /// </summary>
        public Services.RecoverySettings? DeclaredRecovery
        {
            get => this.declaredRecovery;
            set
            {
                if (this.Set(ref this.declaredRecovery, value))
                {
                    this.Raise(nameof(this.RecoveryDiffers));
                    this.Raise(nameof(this.RecoveryDifferenceText));
                }
            }
        }

        public string RecoveryText => this.recovery is { } held
            ? held.Describe(Localizer.Format)
            : Localizer.Get("M.Dash.Recovery.Unknown");

        /// <summary>
        /// Windows does something else on a failure than the file says: changed by hand since the
        /// file was last applied, or a file edited and not applied yet.
        /// </summary>
        public bool RecoveryDiffers =>
            this.recovery is { } held && this.declaredRecovery is { } declared && !held.SameEffectAs(declared);

        public string RecoveryDifferenceText => this.RecoveryDiffers
            ? Localizer.Format("M.Dash.RecoveryDiffers", this.declaredRecovery!.Describe(Localizer.Format))
            : string.Empty;

        /// <summary>
        /// A failure would have Windows start the service again, and nothing stops it doing so yet:
        /// "Stop restarting" has something to stop.
        /// </summary>
        public bool CanStopRestarting => this.recovery?.Restarts == true && this.startType != ServiceStartMode.Disabled;

        /// <summary>
        /// Stopped by a failure Windows answers with a restart, and the restart is not overdue yet:
        /// the service is between two runs of a restart loop, or one restart away from running again,
        /// and Stopped would be the wrong thing to call it. See <see cref="NoteRecovery"/>.
        /// </summary>
        public bool IsRestartingByRecovery
        {
            get => this.restartingByRecovery;
            private set
            {
                if (this.Set(ref this.restartingByRecovery, value))
                {
                    this.Raise(nameof(this.Health));
                    this.Raise(nameof(this.StatusText));
                    this.RaiseAttention();
                }
            }
        }

        /// <summary>
        /// Takes one reading into <see cref="IsRestartingByRecovery"/>. Called after every reading,
        /// full or of states alone, once <see cref="CrashCount"/> has been brought up to date by it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The time of a stop is the time it was first seen: a service found stopped, at the first
        /// reading or after one that could not be made, stopped when nobody was looking, and is not
        /// given a wait it may have finished long ago. A stop seen late, by a reading taken after a
        /// pause, is waited out from when it was seen, which errs towards saying "restarting" a
        /// little too long rather than "stopped" too soon.
        /// </para>
        /// <para>
        /// Re-evaluated at every reading rather than when something changes, because the wait runs
        /// out on its own: nothing about a service still stopped changes when its restart is overdue.
        /// </para>
        /// </remarks>
        public void NoteRecovery(DateTime now)
        {
            if (this.status != ServiceControllerStatus.Stopped)
            {
                this.stoppedAt = null;
            }
            else if (this.recoverySeen is { } seen && seen != ServiceControllerStatus.Stopped)
            {
                this.stoppedAt = now;
            }

            this.recoverySeen = this.status;

            // A disabled service cannot be started, by its recovery or anyone else.
            this.IsRestartingByRecovery =
                this.stoppedAt is { } since
                && this.startType != ServiceStartMode.Disabled
                && this.recovery?.RestartDueBy(this.lastExitCode ?? 0, since, this.crashCount) is { } due
                && now < due;
        }

        // Last stop -------------------------------------------------------------

        /// <summary>The service is stopped: the Last stop card has something to say.</summary>
        public bool IsStopped => this.status == ServiceControllerStatus.Stopped;

        /// <summary>
        /// Why the service stopped, as read for the stop it is in now; null until it has been read,
        /// and from the moment the state or the exit code moves on, which makes it a different stop
        /// or none. Read by the dashboard for the selected service only; see
        /// <see cref="BeginLastStopRead"/>.
        /// </summary>
        public Services.LastStopReport? LastStop
        {
            get => this.lastStop;
            private set
            {
                if (this.Set(ref this.lastStop, value))
                {
                    this.lastStopText = null;
                    this.Raise(nameof(this.LastStopText));
                    this.Raise(nameof(this.IsReadingLastStop));
                }
            }
        }

        /// <summary>
        /// <see cref="LastStop"/> in words, made when first asked for rather than when the report
        /// arrives, and again after a change of language.
        /// </summary>
        public Services.LastStopText? LastStopText =>
            this.lastStop is { } report ? this.lastStopText ??= report.Describe(Localizer.Format) : null;

        /// <summary>Stopped, and why is still being read.</summary>
        public bool IsReadingLastStop => this.IsStopped && this.lastStop is null;

        /// <summary>
        /// Starts a read of <see cref="LastStop"/>, when one is wanted: the service is stopped, this stop
        /// has not been read yet, and no read of it is under way. Returns the stop the read is for,
        /// to hand back with the report, or null for nothing to read.
        /// </summary>
        internal int? BeginLastStopRead()
        {
            if (this.status != ServiceControllerStatus.Stopped || this.lastStop != null || this.lastStopReadFor == this.stopStamp)
            {
                return null;
            }

            this.lastStopReadFor = this.stopStamp;
            return this.stopStamp;
        }

        /// <summary>
        /// Ends the read begun for <paramref name="stop"/>, keeping <paramref name="report"/> if the
        /// service is still in that stop. A null report, a read given up, leaves the stop to be read
        /// again.
        /// </summary>
        internal void EndLastStopRead(int stop, Services.LastStopReport? report)
        {
            if (this.lastStopReadFor == stop)
            {
                this.lastStopReadFor = -1;
            }

            if (report != null && stop == this.stopStamp && this.status == ServiceControllerStatus.Stopped)
            {
                this.LastStop = report;
            }
        }

        /// <summary>
        /// Drops <see cref="LastStop"/>: the stop it described is over, or it is to be read again. A
        /// read still under way for it is dropped with it when it comes back.
        /// </summary>
        public void ForgetLastStop()
        {
            this.stopStamp++;
            this.LastStop = null;
            this.Raise(nameof(this.IsReadingLastStop));
        }

        // Checked after a failed start --------------------------------------------

        /// <summary>
        /// What checking the configuration as the service will run it found — a program its PATH
        /// cannot find, a mapped drive, a virtual environment whose Python has gone — after a start
        /// from this console failed or fell back to stopped straight after; empty otherwise. Shown
        /// on the Last stop card. Kept through the restarts of a loop, each of which is the start
        /// that failed over again, and dropped once a run has held (see <see cref="NoteRun"/>) or a
        /// later check has found something else. See <see cref="ServiceConfigModel.CheckEnvironment(string)"/>.
        /// </summary>
        public ImmutableArray<EnvironmentFinding> StartFindings => this.startFindings;

        /// <summary><see cref="StartFindings"/> in words, in the interface's language, one each.</summary>
        public IReadOnlyList<string> StartFindingsText => this.startFindings.Select(finding => finding.Describe(Localizer.Get)).ToList();

        public bool HasStartFindings => !this.startFindings.IsEmpty;

        /// <summary>
        /// Starts a check after a failed start. Returns the run it is for, to hand back with what it
        /// finds: the check is made off the UI thread, and a run of the service may hold meanwhile.
        /// </summary>
        internal int BeginStartCheck() => this.runStamp;

        /// <summary>
        /// Keeps what the check begun for <paramref name="run"/> found, unless a run of the service
        /// has held since, which makes it about a start that is over.
        /// </summary>
        internal void EndStartCheck(int run, ImmutableArray<EnvironmentFinding> findings)
        {
            if (run == this.runStamp)
            {
                this.SetStartFindings(findings.IsDefault ? ImmutableArray<EnvironmentFinding>.Empty : findings);
            }
        }

        private void SetStartFindings(ImmutableArray<EnvironmentFinding> findings)
        {
            if (this.startFindings.IsEmpty && findings.IsEmpty)
            {
                return;
            }

            this.startFindings = findings;
            this.Raise(nameof(this.StartFindings));
            this.Raise(nameof(this.StartFindingsText));
            this.Raise(nameof(this.HasStartFindings));
        }

        /// <summary>
        /// Takes one reading into how long the service has been running, which is what ends
        /// <see cref="StartFindings"/>: a run that has held for <see cref="Services.CrashAnnouncer.RecoveredAfter"/>,
        /// the time the crash announcer tells a recovery by, is a start that worked. Called after
        /// every reading, full or of states alone.
        /// </summary>
        /// <remarks>
        /// Not when the service is seen starting, which is when they used to be dropped: in a
        /// restart loop that is Windows' recovery starting it again seconds after the check, and
        /// each of those starts fails as the checked one did, so the card went blank for the rest
        /// of the loop. A reading that could not be made is passed over, as the crash announcer
        /// passes it over, rather than starting the clock again.
        /// </remarks>
        public void NoteRun(DateTime now)
        {
            if (this.status is not { } state)
            {
                return;
            }

            if (state != ServiceControllerStatus.Running)
            {
                this.runningSince = null;
                this.runHeld = false;
                return;
            }

            this.runningSince ??= now;
            if (!this.runHeld && now - this.runningSince.Value >= Services.CrashAnnouncer.RecoveredAfter)
            {
                // Once a run. A check still out was begun before the run held, and is about a
                // start that is over by the time it comes back.
                this.runHeld = true;
                this.runStamp++;
                this.SetStartFindings(ImmutableArray<EnvironmentFinding>.Empty);
            }
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
                    this.Raise(nameof(this.LastExitCodeSystemText));
                    this.RaiseAttention();

                    // A new code with the status unchanged is a new stop, between two readings.
                    this.ForgetLastStop();
                }
            }
        }

        public string LastExitCodeText => this.lastExitCode is int code && code != 0 ? code.ToString() : "0";

        /// <summary>Windows' own text for <see cref="LastExitCode"/>, such as "The process terminated unexpectedly." for 1067; empty for 0.</summary>
        public string LastExitCodeSystemText => this.lastExitCode is int code ? Services.LastStopReader.SystemText(code) : string.Empty;

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
                    this.RaiseAttention();
                    this.Raise(nameof(this.StatusText));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.CanStop));
                    this.Raise(nameof(this.IsStopped));
                    this.ForgetLastStop();
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
                    this.RaiseAttention();
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

        /// <summary>
        /// The program the configuration runs, full path; null when it is named bare or cannot be
        /// read. Brought forward by each rescan; the poll reads it off the UI thread.
        /// </summary>
        public string? ExecutablePath
        {
            get => this.executablePath;
            set => this.Set(ref this.executablePath, value);
        }

        /// <summary>
        /// What the service's runs have had under their wrappers and still run, this run's and
        /// earlier ones', as the last full reading left it: what a leftover is recognised by. Not
        /// emptied when a new wrapper starts; see <see cref="Services.StrayWatch"/>. Read off the UI
        /// thread by the poll, which is why it is immutable.
        /// </summary>
        public ImmutableArray<Services.ProcessMark> RememberedProcesses
        {
            get => this.rememberedProcesses;
            set => this.rememberedProcesses = value.IsDefault ? ImmutableArray<Services.ProcessMark>.Empty : value;
        }

        /// <summary>
        /// When the wrapper last seen running started, kept after it stops, unlike
        /// <see cref="StartedAt"/>: a service that is stopping no longer says which wrapper it is,
        /// and what started before this is an earlier run's. See <see cref="Services.StrayWatch"/>.
        /// </summary>
        public DateTime? RunStartedAt { get; set; }

        /// <summary>
        /// The service's program, still running though the service is stopped, or whatever holds a
        /// port it last listened on; see <see cref="Services.StrayProcesses"/>. While the service runs
        /// or is stopping, what an earlier run of it left running beside it; see <see cref="Services.StrayWatch"/>.
        /// </summary>
        public Services.StrayFinding? StrayProcess
        {
            get => this.strayProcess;
            private set
            {
                if (this.Set(ref this.strayProcess, value))
                {
                    this.Raise(nameof(this.HasStrayProcess));
                    this.Raise(nameof(this.CanEndStray));
                    this.Raise(nameof(this.StrayProcessText));
                    this.Raise(nameof(this.StrayParentText));
                    this.Raise(nameof(this.StrayHintText));
                    this.Raise(nameof(this.CanEndStrayParent));
                    this.Raise(nameof(this.StrayParentActionText));
                    this.RaiseAttention();
                }
            }
        }

        /// <summary>
        /// Takes one reading of what the service's runs have left running. A process is called
        /// stray only once it has been there for a few seconds: a clean stop can leave the
        /// program's own children a moment to exit, and a warning that flashed on every stop
        /// would be the one nobody reads when it matters.
        /// </summary>
        /// <remarks>
        /// Something holding the service's port that was never seen under its wrapper is not one of
        /// those children, and is shown at once. The wait would otherwise hide it for good in the
        /// very case it is found for: a service failing on its port and restarted by Windows can
        /// spend less than the wait stopped between two runs, and every reading in between that sees
        /// it running starts the wait over.
        /// </remarks>
        public void NoteStray(Services.StrayFinding? seen, DateTime now)
        {
            if (seen is not { } finding)
            {
                this.strayCandidate = null;
                this.StrayProcess = null;
                return;
            }

            bool settled = finding.HoldsPort && !this.WasUnderWrapper(finding.Process);

            // The same process is judged by its ID and start alone. Its parent may exit between
            // two readings, and that is news for the banner, not a new process to wait out.
            if (this.strayCandidate is not { } candidate || !candidate.Process.IsSameProcessAs(finding.Process))
            {
                this.strayCandidate = finding;
                this.strayCandidateSince = now;
                this.StrayProcess = settled || (this.strayProcess is { } shown && shown.Process.IsSameProcessAs(finding.Process)) ? finding : null;
                return;
            }

            this.strayCandidate = finding;
            if (settled || now - this.strayCandidateSince >= StrayConfirmation || this.strayProcess != null)
            {
                this.StrayProcess = finding;
            }
        }

        /// <summary>The process was among those noted under the service's wrapper, in this run or an earlier one.</summary>
        private bool WasUnderWrapper(Services.ProcessMark process)
        {
            foreach (var noted in this.rememberedProcesses)
            {
                if (noted.IsSameProcessAs(process))
                {
                    return true;
                }
            }

            return false;
        }

        public bool HasStrayProcess => this.strayProcess != null;

        /// <summary>
        /// The process may be offered for ending: not one of Windows' own, and not the kernel, which
        /// is what a port taken through HTTP.sys shows as held by. See <see cref="Services.StrayProcesses.MayEnd"/>.
        /// Nor this console's own try run, which the editor stops, and which the banner points to.
        /// </summary>
        public bool CanEndStray => this.strayProcess is { TryRun: false } stray && Services.StrayProcesses.MayEnd(stray.Process);

        /// <summary>What was found: "api.exe is still running, outside the service", or "port 8000 is held by …".</summary>
        public string StrayProcessText => this.DescribeStray()?.Banner ?? string.Empty;

        /// <summary>What to make of it, under the banner's two lines.</summary>
        public string StrayHintText => this.DescribeStray()?.Hint ?? string.Empty;

        /// <summary>
        /// The parent is still running and is not one of Windows' own: ending it, and so what it
        /// keeps starting, can be offered. Not for a process that may not be ended itself, whose
        /// banner says so in place of who started it.
        /// </summary>
        public bool CanEndStrayParent => this.CanEndStray && this.strayProcess is { Parent: { } parent } && Services.StrayProcesses.MayEnd(parent);

        public string StrayParentActionText => this.strayProcess is { Parent: { } parent }
            ? Localizer.Format("M.Dash.StrayEndParent", parent.Name, parent.ProcessId)
            : string.Empty;

        /// <summary>
        /// Who started the stray process: the answer to "why does it keep coming back" when the
        /// parent is still running, and a true orphan when it is not.
        /// </summary>
        public string StrayParentText => this.DescribeStray()?.Parent ?? string.Empty;

        private Services.StrayText? DescribeStray() => this.strayProcess?.Describe(this.ServiceName, Localizer.Format);

        /// <summary>
        /// What the service's wrapper and the processes under it listen on, lowest port first. Empty
        /// while it is not running, and when the last reading had no reason to read the ports.
        /// </summary>
        public ImmutableArray<Services.ListeningPort> ListeningPorts
        {
            get => this.listeningPorts;
            set
            {
                var ports = value.IsDefault ? ImmutableArray<Services.ListeningPort>.Empty : value;

                // A fresh array every poll, and nearly always the same ports in it.
                if (!this.listeningPorts.AsSpan().SequenceEqual(ports.AsSpan()))
                {
                    this.listeningPorts = ports;
                    this.Raise();
                    this.Raise(nameof(this.ListeningText));
                }
            }
        }

        /// <summary>"0.0.0.0:8000, [::]:8000"; empty, which hides the row, when nothing is listened on.</summary>
        public string ListeningText => Services.PortTable.Describe(this.listeningPorts);

        /// <summary>
        /// Stops in the counting window now open, the one told at once included; 0 when none is
        /// open. 1 when the first crash is announced. See <see cref="Services.CrashAnnouncer"/>.
        /// </summary>
        public int CrashCount
        {
            get => this.crashCount;
            set => this.Set(ref this.crashCount, value);
        }

        // Needs attention ----------------------------------------------------

        /// <summary>
        /// How many times the service has stopped lately without this console asking it to, as the
        /// crash announcer counts them; see <see cref="Services.CrashAnnouncer.RecentStopsFor"/>. 0
        /// when it has not, or not for a whole window. Set by the dashboard at every reading.
        /// </summary>
        public int RecentStops
        {
            get => this.recentStops;
            set
            {
                if (this.Set(ref this.recentStops, value))
                {
                    this.RaiseAttention();
                }
            }
        }

        /// <summary>
        /// Why the service wants a look, if it does: what the Needs attention card counts and
        /// shows, what "sort by status" puts first, and what the row's tooltip says.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Wider than <see cref="Health"/>'s Broken, which only an unusable configuration makes. A
        /// crashed service, one in a restart loop and one whose program still runs outside it all
        /// read as plain Stopped or Running there, and hid in those counts while the red card
        /// said nothing was wrong. Health is left as it is: it colours the row, and the stopped
        /// and running counts are by it.
        /// </para>
        /// <para>
        /// A service Windows' recovery is about to start again is not also flagged for its exit
        /// code or its start type: they describe the same stop, which is about to end. A stopped
        /// service's exit code 1077 says only that nobody has started it since the machine
        /// booted, which for a manual service is nothing to look at.
        /// </para>
        /// </remarks>
        public AttentionReasons Attention
        {
            get
            {
                var reasons = AttentionReasons.None;
                if (this.problem != null)
                {
                    reasons |= AttentionReasons.ConfigProblem;
                }

                if (this.strayProcess != null)
                {
                    reasons |= AttentionReasons.StrayProcess;
                }

                if (this.restartingByRecovery)
                {
                    reasons |= AttentionReasons.RestartingByRecovery;
                }

                if (this.recentStops > 0)
                {
                    reasons |= AttentionReasons.RecentStops;
                }

                if (this.status == ServiceControllerStatus.Stopped && !this.restartingByRecovery)
                {
                    if (this.lastExitCode is int code && code != 0 && code != Services.NativeMethods.ERROR_SERVICE_NEVER_STARTED)
                    {
                        reasons |= AttentionReasons.StoppedWithError;
                    }

                    // Not a manual service, which is stopped until somebody wants it, nor a
                    // disabled one, which is stopped on purpose.
                    if (this.startType == ServiceStartMode.Automatic)
                    {
                        reasons |= AttentionReasons.AutomaticButStopped;
                    }
                }

                return reasons;
            }
        }

        public bool NeedsAttention => this.Attention != AttentionReasons.None;

        /// <summary>Each reason in <see cref="Attention"/> on a line of its own, for the row's tooltip; null, and so no tooltip, when there is none.</summary>
        public string? AttentionText => this.NeedsAttention
            ? this.DescribeAttention(Localizer.Format, Services.LastStopReader.SystemText)
            : null;

        /// <summary>
        /// <see cref="Attention"/> in words, one line per reason: the problem and the stray banner as
        /// the detail panel words them, the rest from <paramref name="format"/>.
        /// </summary>
        /// <param name="format">Looks a phrase up by key and fills it in: <c>Localizer.Format</c>.</param>
        /// <param name="systemText">Windows' own text for an exit code: <see cref="Services.LastStopReader.SystemText"/>.</param>
        internal string DescribeAttention(Func<string, object?[], string> format, Func<int, string> systemText)
        {
            var reasons = this.Attention;
            var lines = new List<string>();

            if (reasons.HasFlag(AttentionReasons.ConfigProblem))
            {
                lines.Add(this.problem!);
            }

            if (reasons.HasFlag(AttentionReasons.StrayProcess))
            {
                lines.Add(this.strayProcess!.Value.Describe(this.ServiceName, format).Banner);
            }

            if (reasons.HasFlag(AttentionReasons.RestartingByRecovery))
            {
                lines.Add(format("M.Attention.Restarting", Array.Empty<object?>()));
            }

            if (reasons.HasFlag(AttentionReasons.RecentStops))
            {
                lines.Add(format("M.Attention.RecentStops", new object?[] { this.recentStops }));
            }

            if (reasons.HasFlag(AttentionReasons.StoppedWithError))
            {
                int code = this.lastExitCode!.Value;
                lines.Add(format("M.Attention.ExitCode", new object?[] { code, systemText(code) }).TrimEnd());
            }

            if (reasons.HasFlag(AttentionReasons.AutomaticButStopped))
            {
                lines.Add(format("M.Attention.AutomaticStopped", Array.Empty<object?>()));
            }

            return string.Join("\n", lines);
        }

        private void RaiseAttention()
        {
            this.Raise(nameof(this.Attention));
            this.Raise(nameof(this.NeedsAttention));
            this.Raise(nameof(this.AttentionText));
            this.Raise(nameof(this.SortRank));
        }

        /// <summary>
        /// Order for "sort by status": what needs a look first, whatever its state, then what is on
        /// its way to a state, then stopped, then running.
        /// </summary>
        public int SortRank => this.NeedsAttention ? 0 : this.Health switch
        {
            ServiceHealth.Pending => 1,
            ServiceHealth.Stopped => 2,
            ServiceHealth.Running => 3,
            _ => 4,
        };

        /// <remarks>
        /// A service Windows is about to start again is Pending, not Stopped: it is on its way to a
        /// state, as a service starting is, and the colour and the stopped count say so.
        /// </remarks>
        public ServiceHealth Health => this.problem != null ? ServiceHealth.Broken : this.restartingByRecovery ? ServiceHealth.Pending : this.status switch
        {
            ServiceControllerStatus.Running => ServiceHealth.Running,
            ServiceControllerStatus.Stopped => ServiceHealth.Stopped,
            null => ServiceHealth.Unknown,
            _ => ServiceHealth.Pending,
        };

        public string StatusText => this.restartingByRecovery ? Localizer.Get("M.Status.Restarting") : Localizer.Get(this.status switch
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
            this.Raise(nameof(this.StrayProcessText));
            this.Raise(nameof(this.StrayParentText));
            this.Raise(nameof(this.StrayHintText));
            this.Raise(nameof(this.StrayParentActionText));
            this.Raise(nameof(this.RecoveryText));
            this.Raise(nameof(this.RecoveryDifferenceText));
            this.Raise(nameof(this.StartUnavailableTip));
            this.Raise(nameof(this.AttentionText));
            this.Raise(nameof(this.StartFindingsText));

            // Made in the language it was first asked for in.
            if (this.lastStopText != null)
            {
                this.lastStopText = null;
                this.Raise(nameof(this.LastStopText));
            }

            // Stored as text, because a rescan brings it forward as text; said again from the
            // start type it was made from, as the rescan would, rather than waiting for one.
            this.StartMode = Services.ServiceDiscovery.DescribeStartMode(this.startType, this.delayedAutoStart);
        }

        /// <summary>
        /// Windows will start the service when asked. A disabled one it refuses with error 1058,
        /// whoever asks, so neither Start nor Restart is offered for it: a restart would stop a
        /// running service and then fail to start it again.
        /// </summary>
        public bool IsStartable => this.startType != ServiceStartMode.Disabled;

        public bool CanStart => this.status == ServiceControllerStatus.Stopped && this.IsStartable;

        /// <summary>
        /// Why Start and Restart are not offered, for their tooltips; null, and so no tooltip,
        /// while nothing about the service itself stands in their way.
        /// </summary>
        public string? StartUnavailableTip => this.IsStartable ? null : Localizer.Get("M.Dash.StartDisabledTip");

        public bool CanStop => this.status == ServiceControllerStatus.Running
            || this.status == ServiceControllerStatus.Paused;

        /// <summary>The directory logs default to when the configuration does not override it.</summary>
        public string? DefaultLogDirectory =>
            this.ConfigPath is null ? null : Path.GetDirectoryName(this.ConfigPath);

        public override string ToString() => this.ServiceName;
    }
}
