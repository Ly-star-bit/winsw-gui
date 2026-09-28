using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One log file belonging to a service.</summary>
    /// <remarks>
    /// Observable because the list is brought up to date in place while a file is on screen:
    /// an entry replaced by a new one would make the picker drop its selection, and the viewer
    /// start the file over.
    /// </remarks>
    public sealed class LogFileEntry : ObservableObject
    {
        private long length;
        private DateTime lastWrite;

        public LogFileEntry(FileInfo file)
        {
            this.Path = file.FullName;
            this.Name = file.Name;
            this.length = file.Length;
            this.lastWrite = file.LastWriteTime;
        }

        public string Path { get; }

        public string Name { get; }

        public long Length => this.length;

        public DateTime LastWrite => this.lastWrite;

        public string Caption =>
            $"{this.Name}   ·   {FormatSize(this.Length)}   ·   {this.LastWrite:yyyy-MM-dd HH:mm:ss}";

        internal static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        };

        public override string ToString() => this.Caption;

        /// <summary>
        /// The files cleanup would take: written before <paramref name="cutoff"/>, and not the
        /// one on screen, which is being read and is not deleted from under its reader.
        /// </summary>
        internal static IReadOnlyList<LogFileEntry> OlderThan(IEnumerable<LogFileEntry> files, DateTime cutoff, string? onScreen) =>
            files.Where(f => f.LastWrite < cutoff && !string.Equals(f.Path, onScreen, StringComparison.OrdinalIgnoreCase)).ToList();

        /// <summary>
        /// The service's log files in <paramref name="directory"/>, newest first: everything
        /// the appenders may produce for it — .out.log, .err.log, .wrapper.log and the numbered
        /// or dated files the rolling modes add.
        /// </summary>
        internal static IReadOnlyList<LogFileEntry> Scan(string directory, string stem) =>
            new DirectoryInfo(directory)
                .EnumerateFiles(stem + "*")
                .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                    || f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new LogFileEntry(f))
                .ToList();

        /// <summary>
        /// Brings <paramref name="files"/> in line with a fresh <see cref="Scan"/>, in place:
        /// entries still there take their new size and time, new files go where newest-first
        /// puts them, and files that are gone are dropped. The entry on screen is never dropped
        /// or replaced, even when the scan no longer finds it — in roll mode it is missing for
        /// the moment between the wrapper's rename and its new file.
        /// </summary>
        /// <returns>The entries added.</returns>
        internal static IReadOnlyList<LogFileEntry> Merge(IList<LogFileEntry> files, IReadOnlyList<LogFileEntry> found, LogFileEntry? onScreen)
        {
            var unseen = new Dictionary<string, LogFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in found)
            {
                unseen[entry.Path] = entry;
            }

            for (int i = files.Count - 1; i >= 0; i--)
            {
                var entry = files[i];
                if (unseen.Remove(entry.Path, out var scanned))
                {
                    entry.Update(scanned);
                }
                else if (!ReferenceEquals(entry, onScreen))
                {
                    files.RemoveAt(i);
                }
            }

            var added = new List<LogFileEntry>();
            foreach (var entry in found)
            {
                if (!unseen.ContainsKey(entry.Path))
                {
                    continue;
                }

                int at = 0;
                while (at < files.Count && files[at].LastWrite >= entry.LastWrite)
                {
                    at++;
                }

                files.Insert(at, entry);
                added.Add(entry);
            }

            return added;
        }

        /// <summary>
        /// Of the entries a <see cref="Merge"/> added to <paramref name="files"/>, those written
        /// later than every file listed before: the files a new period of roll-by-time starts.
        /// A file rolled away by size is never among them, since the file that took over its
        /// name was created after it was last written — so a busy .err.log rolling beside a
        /// quiet .out.log is not news. Nothing, when nothing was listed before.
        /// </summary>
        internal static IReadOnlyList<LogFileEntry> NewerThanTheRest(IEnumerable<LogFileEntry> files, IReadOnlyList<LogFileEntry> added)
        {
            var before = files.Where(f => !added.Contains(f)).ToList();
            if (before.Count == 0)
            {
                return Array.Empty<LogFileEntry>();
            }

            DateTime latest = before.Max(f => f.LastWrite);
            return added.Where(f => f.LastWrite > latest).ToList();
        }

        /// <summary>Takes the size and time a later scan found for the same file.</summary>
        private void Update(LogFileEntry scanned)
        {
            if (this.length == scanned.length && this.lastWrite == scanned.lastWrite)
            {
                return;
            }

            this.length = scanned.length;
            this.lastWrite = scanned.lastWrite;
            this.Raise(nameof(this.Length));
            this.Raise(nameof(this.LastWrite));
            this.Raise(nameof(this.Caption));
        }
    }

    /// <summary>A selectable log encoding, labelled for the picker.</summary>
    public sealed class EncodingOption : ObservableObject
    {
        private readonly string key;

        public EncodingOption(LogEncodingChoice choice, string key)
        {
            this.Choice = choice;
            this.key = key;
        }

        public LogEncodingChoice Choice { get; }

        public string Label => Localizer.Get(this.key);

        public void RefreshLocalized() => this.Raise(nameof(this.Label));
    }

    /// <summary>
    /// Tails the log files a service produces and shows the Windows events about it.
    /// </summary>
    /// <remarks>
    /// File names are discovered by scanning the log directory rather than by reproducing
    /// the appenders' naming rules. Those rules differ per log mode — and
    /// <c>SimpleLogAppender</c> hard-codes <c>.out.log</c>/<c>.err.log</c> while ignoring the
    /// configured patterns — so scanning is both simpler and correct for rolled files such as
    /// <c>service.1.log</c>.
    /// </remarks>
    public sealed class LogViewerViewModel : ObservableObject, IDisposable
    {
        private const int MaxLines = 5000;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

        /// <summary>How often the log directory is looked at again while the page is shown.</summary>
        private static readonly TimeSpan FileScanInterval = TimeSpan.FromSeconds(30);

        private readonly DispatcherTimer timer;

        /// <summary>The lines held, the ones of them shown, and the error count of those.</summary>
        private readonly LogLineBuffer buffer;

        private LogTailReader? reader;

        /// <summary>
        /// The reader a worker is currently inside. Nothing on the UI thread disposes it
        /// while it is set; whatever replaces it is closed by <see cref="PumpAsync"/> itself
        /// once the read returns.
        /// </summary>
        private LogTailReader? inFlight;
        private ServiceEntry? service;
        private LogFileEntry? selectedFile;
        private EncodingOption selectedEncoding;
        private string logDirectory = string.Empty;
        private string filter = string.Empty;
        private string statusMessage = string.Empty;
        private string encodingInfo = string.Empty;
        private string eventsStatus = string.Empty;
        private bool autoScroll = true;
        private bool isPaused;
        private bool isLoadingEvents;
        private string filterNeedle = string.Empty;
        private bool useRegex;
        private bool wrapLines = AppSettings.Current.LogWrapLines;
        private double fontSize = AppSettings.Current.LogFontSize;
        private ServiceEntry? selectedService;
        private System.Text.RegularExpressions.Regex? filterRegex;
        private bool filterInvalid;
        private int errorCount;
        private int lastJumpIndex = -1;
        private string keepDays = "30";
        private bool cleanupConfirmVisible;
        private string cleanupMessage = string.Empty;
        private bool isCleaning;
        private IReadOnlyList<LogFileEntry> cleanupCandidates = Array.Empty<LogFileEntry>();

        /// <summary>The file-name stem the list was last scanned for, in <see cref="LogDirectory"/>.</summary>
        private string logStem = string.Empty;

        /// <summary>When the list was last scanned, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
        private long lastFileScan;
        private bool isScanning;

        /// <summary>Whether the page is the one shown. Only then is the file on screen held open and read.</summary>
        private bool isActive;

        public LogViewerViewModel()
        {
            this.buffer = new LogLineBuffer(MaxLines, line => this.IsVisible(line.Text));
            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += async (_, _) =>
            {
                if (!this.isPaused && Environment.TickCount64 - this.lastFileScan >= (long)FileScanInterval.TotalMilliseconds)
                {
                    _ = this.RefreshFilesAsync();
                }

                await this.PumpAsync().ConfigureAwait(true);
            };

            this.Encodings = new[]
            {
                new EncodingOption(LogEncodingChoice.Auto, "M.Enc.Auto"),
                new EncodingOption(LogEncodingChoice.Utf8, "M.Enc.Utf8"),
                new EncodingOption(LogEncodingChoice.SystemAnsi, "M.Enc.Ansi"),
            };
            this.selectedEncoding = this.Encodings.FirstOrDefault(e => e.Choice == AppSettings.Current.LogEncoding) ?? this.Encodings[0];

            this.RescanCommand = new RelayCommand(this.Rescan, () => this.service != null);
            this.ClearCommand = new RelayCommand(this.ClearLines);

            this.TogglePauseCommand = new RelayCommand(() => this.IsPaused = !this.IsPaused);
            this.OpenExternallyCommand = new RelayCommand(this.OpenExternally, () => this.selectedFile != null);
            this.RevealCommand = new RelayCommand(this.Reveal, () => this.selectedFile != null);
            this.RefreshEventsCommand = new AsyncRelayCommand(this.LoadEventsAsync, () => this.service != null && !this.isLoadingEvents);
            this.NextErrorCommand = new RelayCommand(this.JumpToNextError, () => this.errorCount > 0);
            this.CleanupCommand = new RelayCommand(this.AskCleanup, () => this.Files.Count > 0 && !this.isCleaning);
            this.ConfirmCleanupCommand = new AsyncRelayCommand(this.CleanUpAsync);
            this.CancelCleanupCommand = new RelayCommand(() => this.CleanupConfirmVisible = false);

            this.statusMessage = Localizer.Get("M.Log.SelectService");
            Localizer.Changed += () =>
            {
                this.Raise(nameof(this.ServiceName));
                this.Raise(nameof(this.PauseLabel));
                foreach (var option in this.Encodings)
                {
                    option.RefreshLocalized();
                }
            };
        }

        /// <summary>Raised after a batch of lines is appended, so the view scrolls once per batch.</summary>
        public event Action? LinesAppended;

        public ObservableCollection<LogFileEntry> Files { get; } = new();

        /// <summary>
        /// The lines currently shown, which is the buffer with the filter applied. Bulk so
        /// that re-filtering announces itself once rather than once per line.
        /// </summary>
        /// <remarks>
        /// Each line its own item, not its text: the view scrolls and selects by item, and a
        /// restart loop writes the same lines again and again.
        /// </remarks>
        public BulkObservableCollection<LogLine> Lines => this.buffer.Visible;

        public ObservableCollection<ServiceEvent> Events { get; } = new();

        public EncodingOption[] Encodings { get; }

        public RelayCommand RescanCommand { get; }

        // Cleanup ---------------------------------------------------------------

        /// <summary>How many files the service has here and how much they come to; nothing new is read for it.</summary>
        public string TotalSizeText => this.Files.Count == 0
            ? string.Empty
            : Localizer.Format("M.Log.TotalSize", this.Files.Count, LogFileEntry.FormatSize(this.Files.Sum(f => f.Length)));

        /// <summary>Files last written longer ago than this many days are the ones cleanup deletes.</summary>
        public string KeepDays
        {
            get => this.keepDays;
            set => this.Set(ref this.keepDays, value);
        }

        public RelayCommand CleanupCommand { get; }

        public AsyncRelayCommand ConfirmCleanupCommand { get; }

        public RelayCommand CancelCleanupCommand { get; }

        public bool CleanupConfirmVisible
        {
            get => this.cleanupConfirmVisible;
            private set => this.Set(ref this.cleanupConfirmVisible, value);
        }

        public string CleanupMessage
        {
            get => this.cleanupMessage;
            private set => this.Set(ref this.cleanupMessage, value);
        }

        private void AskCleanup()
        {
            if (!int.TryParse(this.keepDays.Trim(), out int days) || days < 1)
            {
                this.StatusMessage = Localizer.Format("M.Log.CleanupBadDays", this.keepDays);
                return;
            }

            var old = LogFileEntry.OlderThan(this.Files, DateTime.Now - TimeSpan.FromDays(days), this.selectedFile?.Path);
            if (old.Count == 0)
            {
                this.StatusMessage = Localizer.Format("M.Log.CleanupNone", days);
                return;
            }

            this.cleanupCandidates = old;
            this.CleanupMessage = Localizer.Format("M.Log.CleanupBody", old.Count, days, LogFileEntry.FormatSize(old.Sum(f => f.Length)), this.LogDirectory);
            this.CleanupConfirmVisible = true;
        }

        private async Task CleanUpAsync()
        {
            this.CleanupConfirmVisible = false;
            var targets = this.cleanupCandidates;
            this.cleanupCandidates = Array.Empty<LogFileEntry>();
            if (targets.Count == 0)
            {
                return;
            }

            this.isCleaning = true;
            this.CleanupCommand.RaiseCanExecuteChanged();
            try
            {
                var outcome = await LogCleanup.DeleteAsync(targets.Select(t => (t.Path, t.Length)).ToList()).ConfigureAwait(true);

                string done = Localizer.Format("M.Log.CleanupDone", outcome.Deleted, LogFileEntry.FormatSize(outcome.Freed));
                this.StatusMessage = outcome.Declined && outcome.Deleted == 0
                    ? Localizer.Get("M.Common.ElevationDeclined")
                    : outcome.Left > 0 ? done + " " + Localizer.Format("M.Log.CleanupLeft", outcome.Left) : done;

                ActionLog.Record(
                    "delete logs",
                    this.ServiceName,
                    outcome.Left == 0
                        ? $"ok: {outcome.Deleted} files, {LogFileEntry.FormatSize(outcome.Freed)}"
                        : $"{outcome.Deleted} deleted, {outcome.Left} left" + (outcome.Declined ? " (elevation declined)" : string.Empty));
            }
            finally
            {
                this.isCleaning = false;
                string status = this.StatusMessage;
                this.Rescan();

                // The rescan reports the files it found; what was just done is the more useful line.
                this.StatusMessage = status;
            }
        }

        public RelayCommand ClearCommand { get; }

        public RelayCommand TogglePauseCommand { get; }

        public RelayCommand OpenExternallyCommand { get; }

        public RelayCommand RevealCommand { get; }

        public AsyncRelayCommand RefreshEventsCommand { get; }

        public RelayCommand NextErrorCommand { get; }

        /// <summary>Installed services, so a service can be picked here as well as from the dashboard.</summary>
        public IEnumerable<ServiceEntry> Services { get; set; } = Array.Empty<ServiceEntry>();

        public ServiceEntry? SelectedService
        {
            get => this.selectedService;
            set
            {
                if (this.Set(ref this.selectedService, value) && value != null && !ReferenceEquals(value, this.service))
                {
                    this.Attach(value);
                }
            }
        }

        public bool WrapLines
        {
            get => this.wrapLines;
            set
            {
                if (this.Set(ref this.wrapLines, value))
                {
                    AppSettings.Current.LogWrapLines = value;
                    AppSettings.Current.Save();
                }
            }
        }

        public double FontSize
        {
            get => this.fontSize;
            set
            {
                double clamped = Math.Clamp(value, 9, 24);
                if (this.Set(ref this.fontSize, clamped))
                {
                    AppSettings.Current.LogFontSize = clamped;
                    AppSettings.Current.Save();
                }
            }
        }

        /// <summary>Raised with the index of a line the view should bring into view and highlight.</summary>
        public event Action<int>? ScrollToRequested;

        /// <summary>Interpret <see cref="Filter"/> as a .NET regular expression instead of plain text.</summary>
        public bool UseRegex
        {
            get => this.useRegex;
            set
            {
                if (this.Set(ref this.useRegex, value))
                {
                    this.CompileFilter();
                    this.RebuildVisibleLines();
                }
            }
        }

        /// <summary>True when the regex does not compile; the filter then matches nothing.</summary>
        public bool FilterInvalid
        {
            get => this.filterInvalid;
            private set => this.Set(ref this.filterInvalid, value);
        }

        /// <summary>Error-looking lines among those visible.</summary>
        public int ErrorCount
        {
            get => this.errorCount;
            private set
            {
                if (this.Set(ref this.errorCount, value))
                {
                    this.NextErrorCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string ServiceName => this.service?.ServiceName ?? Localizer.Get("M.Log.NoService");

        public string LogDirectory
        {
            get => this.logDirectory;
            private set => this.Set(ref this.logDirectory, value);
        }

        public LogFileEntry? SelectedFile
        {
            get => this.selectedFile;
            set
            {
                if (this.Set(ref this.selectedFile, value))
                {
                    this.OpenExternallyCommand.RaiseCanExecuteChanged();
                    this.RevealCommand.RaiseCanExecuteChanged();
                    this.OpenSelected();
                }
            }
        }

        public EncodingOption SelectedEncoding
        {
            get => this.selectedEncoding;
            set
            {
                if (value != null && this.Set(ref this.selectedEncoding, value))
                {
                    AppSettings.Current.LogEncoding = value.Choice;
                    AppSettings.Current.Save();
                    this.OpenSelected();
                }
            }
        }

        /// <summary>What auto-detection settled on for the open file.</summary>
        public string EncodingInfo
        {
            get => this.encodingInfo;
            private set => this.Set(ref this.encodingInfo, value);
        }

        /// <summary>Case-insensitive substring filter applied to the buffered lines.</summary>
        public string Filter
        {
            get => this.filter;
            set
            {
                if (this.Set(ref this.filter, value))
                {
                    this.CompileFilter();
                    this.RebuildVisibleLines();
                }
            }
        }

        public bool AutoScroll
        {
            get => this.autoScroll;
            set => this.Set(ref this.autoScroll, value);
        }

        public bool IsPaused
        {
            get => this.isPaused;
            set
            {
                if (this.Set(ref this.isPaused, value))
                {
                    this.Raise(nameof(this.PauseLabel));

                    // A paused viewer reads nothing, so it holds nothing open either: left paused
                    // overnight, it would hold a file the wrapper keeps rolling down its chain.
                    if (value)
                    {
                        this.ReleaseReader();
                    }
                }
            }
        }

        public string PauseLabel => Localizer.Get(this.isPaused ? "M.Log.Resume" : "M.Log.Pause");

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        public string EventsStatus
        {
            get => this.eventsStatus;
            private set => this.Set(ref this.eventsStatus, value);
        }

        public bool IsLoadingEvents
        {
            get => this.isLoadingEvents;
            private set
            {
                if (this.Set(ref this.isLoadingEvents, value))
                {
                    this.RefreshEventsCommand.RaiseCanExecuteChanged();
                }
            }
        }

        // Lifetime --------------------------------------------------------------

        /// <summary>
        /// Points the viewer at a configuration that no installed service owns — a desktop
        /// task's, typically. The log files are found the same way either way: they are
        /// described by the configuration, not by the thing that started the wrapper.
        /// </summary>
        public void AttachConfiguration(string configPath, string caption)
        {
            this.Attach(new ServiceEntry(caption, caption, string.Empty, configPath));
        }

        public void Attach(ServiceEntry entry)
        {
            this.service = entry;
            this.selectedService = entry;
            this.Raise(nameof(this.SelectedService));
            this.Raise(nameof(this.ServiceName));
            this.RescanCommand.RaiseCanExecuteChanged();
            this.RefreshEventsCommand.RaiseCanExecuteChanged();
            this.Rescan(fresh: true);
            this.RefreshEventsCommand.Execute(null);

            if (this.isActive)
            {
                this.timer.Start();
            }
        }

        public void Activate()
        {
            this.isActive = true;
            if (this.service != null)
            {
                this.timer.Start();

                // The file was let go of when the page was left: open it again at the place it
                // was let go at now, rather than a tick from now.
                _ = this.PumpAsync();
            }
        }

        /// <summary>
        /// Stops polling and lets go of the file on screen, keeping the place in it. A page out
        /// of sight held its file open for as long as the console ran, while the wrapper
        /// renamed that file down its whole chain of rolled logs underneath it.
        /// </summary>
        public void Deactivate()
        {
            this.isActive = false;
            this.timer.Stop();
            this.ReleaseReader();
        }

        // Files ------------------------------------------------------------------

        private void Rescan() => this.Rescan(fresh: false);

        /// <summary>
        /// Looks at the log directory again. The list is brought up to date in place, so the
        /// file on screen stays there with everything it shows; the list starts over only for
        /// another service, or for a configuration that now puts its logs somewhere else.
        /// </summary>
        private void Rescan(bool fresh)
        {
            var entry = this.service;
            if (entry?.ConfigPath is null)
            {
                this.StatusMessage = Localizer.Get("M.Log.NoConfig");
                return;
            }

            try
            {
                var model = ServiceConfigModel.Load(entry.ConfigPath);
                string directory = ConfigPaths.ResolveLogDirectory(model, entry.ConfigPath);
                string stem = ConfigPaths.ResolveLogBaseName(model, entry.ConfigPath);

                string? previous = this.selectedFile?.Path;
                if (fresh || directory != this.LogDirectory || stem != this.logStem)
                {
                    this.SelectedFile = null;
                    this.Files.Clear();
                }

                this.LogDirectory = directory;
                this.logStem = stem;
                this.lastFileScan = Environment.TickCount64;

                bool exists = Directory.Exists(directory);
                IReadOnlyList<LogFileEntry> found = exists ? LogFileEntry.Scan(directory, stem) : Array.Empty<LogFileEntry>();
                LogFileEntry.Merge(this.Files, found, this.selectedFile);

                this.StatusMessage = !exists
                    ? Localizer.Format("M.Log.DirMissing", directory)
                    : this.Files.Count == 0
                        ? Localizer.Format("M.Log.NoFiles", stem, directory)
                        : Localizer.Format("M.Log.Files", this.Files.Count, directory);
                this.Raise(nameof(this.TotalSizeText));
                this.CleanupCommand.RaiseCanExecuteChanged();

                this.SelectedFile ??= this.Files.FirstOrDefault(f => f.Path == previous) ?? this.Files.FirstOrDefault();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                this.StatusMessage = Localizer.Format("M.Log.DirFailed", e.Message);
            }
        }

        /// <summary>
        /// Looks at the log directory again on a worker, every <see cref="FileScanInterval"/>
        /// while the page is shown. roll-by-time renames nothing: it starts the next period's
        /// file under a new name and leaves the one on screen as it was, which then simply went
        /// quiet, and the new file appeared only after a press of the rescan button.
        /// </summary>
        private async Task RefreshFilesAsync()
        {
            var entry = this.service;
            string directory = this.LogDirectory;
            string stem = this.logStem;
            if (this.isScanning || entry is null || stem.Length == 0)
            {
                return;
            }

            this.isScanning = true;
            this.lastFileScan = Environment.TickCount64;
            try
            {
                var found = await Task.Run(() => LogFileEntry.Scan(directory, stem)).ConfigureAwait(true);
                if (!ReferenceEquals(entry, this.service) || directory != this.LogDirectory || stem != this.logStem)
                {
                    // Another service, or another configuration, was put on screen meanwhile.
                    return;
                }

                bool wasEmpty = this.Files.Count == 0;
                var added = LogFileEntry.Merge(this.Files, found, this.selectedFile);
                this.Raise(nameof(this.TotalSizeText));
                this.CleanupCommand.RaiseCanExecuteChanged();

                var onScreen = this.selectedFile;
                if (onScreen is null)
                {
                    // The service had not written anything yet when it was put on screen.
                    if (wasEmpty && this.Files.Count > 0)
                    {
                        this.StatusMessage = Localizer.Format("M.Log.Files", this.Files.Count, directory);
                        this.SelectedFile = this.Files[0];
                    }

                    return;
                }

                // Said where the eye is rather than switched to: which file carries on from which
                // is the appenders' naming, which this viewer deliberately does not reproduce.
                var newer = LogFileEntry.NewerThanTheRest(this.Files, added);
                foreach (var file in newer)
                {
                    this.Append(Localizer.Format("M.Log.NewerFile", file.Name));
                }

                if (newer.Count > 0)
                {
                    this.LinesAppended?.Invoke();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The directory is gone or out of reach for now. The next scan, or the rescan
                // button, tries again; the file on screen carries on meanwhile.
            }
            finally
            {
                this.isScanning = false;
            }
        }

        private void OpenSelected()
        {
            this.CloseReader();
            this.ClearLines();
            this.EncodingInfo = string.Empty;

            if (this.selectedFile is null)
            {
                // The timer keeps running while the page is shown: its file scan is how the
                // first log of a service that has not written one yet turns up.
                return;
            }

            this.reader = new LogTailReader(this.selectedFile.Path, this.selectedEncoding.Choice);
            _ = this.PumpAsync();
        }

        /// <summary>Closes the handle on the log being tailed.</summary>
        public void Dispose()
        {
            this.timer.Stop();
            this.CloseReader();
        }

        /// <summary>
        /// Lets go of the current reader. Closed here unless a worker is inside it, in which
        /// case it is left to the read to close on its way back: disposing a FileStream under
        /// a Read on another thread is an ObjectDisposedException in that thread.
        /// </summary>
        private void CloseReader()
        {
            var current = this.reader;
            this.reader = null;

            if (current != null && !ReferenceEquals(current, this.inFlight))
            {
                current.Dispose();
            }
        }

        /// <summary>
        /// Lets go of the file on screen and keeps the place in it, for a page that is not
        /// reading. When a worker is inside the reader, the read does it on its way back, for
        /// the reason <see cref="CloseReader"/> gives.
        /// </summary>
        private void ReleaseReader()
        {
            if (this.reader != null && !ReferenceEquals(this.reader, this.inFlight))
            {
                this.reader.Release();
            }
        }

        /// <summary>
        /// Reads what the file has gained and puts it on screen.
        /// </summary>
        /// <remarks>
        /// The reading is on a worker; only the applying is here. It ran on this thread, every
        /// six hundred milliseconds, and for a log on a local disk that is a length query and a
        /// short read. For one on a share it is a round trip per tick on the thread that is
        /// drawing, and a share that has stopped answering is that thread stalled until the
        /// redirector gives up. The status poll came off this thread for the same reason.
        /// </remarks>
        private async Task PumpAsync()
        {
            // One read in flight at a time: the reader keeps its own position and buffer,
            // and a second read started over the first would race it for both. Nothing is
            // read for a page out of sight, which has let go of its file.
            if (this.inFlight != null || this.reader is null || this.isPaused || !this.isActive)
            {
                return;
            }

            var reader = this.reader;
            this.inFlight = reader;

            IReadOnlyList<string> lines;
            try
            {
                lines = await Task.Run(reader.ReadNewLines).ConfigureAwait(true);
            }
            finally
            {
                this.inFlight = null;
            }

            if (!ReferenceEquals(reader, this.reader))
            {
                // Another file was chosen while this read was out. What it read belongs to the
                // file that is no longer shown, and the reader was left open for this to close.
                reader.Dispose();
                return;
            }

            if (!this.isActive || this.isPaused)
            {
                // The page was left, or paused, while this read was out, and letting go of the
                // file was left to now. What was read is still shown.
                reader.Release();
            }

            string? rolled = null;
            if (reader.Restarted)
            {
                // The count goes with the lines: it kept counting the errors of a file that
                // was no longer on screen.
                this.ClearLines();
                rolled = Localizer.Get("M.Log.Rolled");
            }

            // The reader passed over more than the buffer could have held. Said on screen, in
            // the place the gap is, rather than leaving a jump in the timestamps to explain.
            string? skipped = reader.SkippedBytes > 0
                ? Localizer.Format("M.Log.Skipped", reader.SkippedBytes)
                : null;

            // The wrapper rolled the file on screen. Nothing shown so far is wrong, so nothing is
            // cleared: the lines just read are the old file's last, and the marker goes after
            // them, where the new file starts from the next read on.
            string? rolledOver = reader.Rollover switch
            {
                LogRollover.ReadToEnd => Localizer.Get("M.Log.RolledOver"),
                LogRollover.WhileReleased => Localizer.Get("M.Log.RolledAway"),
                _ => null,
            };

            if (lines.Count >= MaxLines)
            {
                // A batch this size replaces everything on screen. Built to one side and
                // announced once, the way a filter change is, instead of thousands of adds
                // each followed by the removal of the line it pushed out.
                var batch = new List<LogLine>(MaxLines);
                if (rolled != null)
                {
                    batch.Add(new LogLine(rolled));
                }

                if (skipped != null)
                {
                    batch.Add(new LogLine(skipped));
                }

                int room = MaxLines - batch.Count - (rolledOver is null ? 0 : 1);
                for (int i = lines.Count - room; i < lines.Count; i++)
                {
                    batch.Add(new LogLine(lines[i]));
                }

                if (rolledOver != null)
                {
                    batch.Add(new LogLine(rolledOver));
                }

                this.buffer.ReplaceAll(batch);
                this.EncodingInfo = Localizer.Format("M.Log.Detected", reader.EncodingName);
                this.OnLinesRebuilt();
                return;
            }

            if (rolled != null)
            {
                this.Append(rolled);
            }

            if (skipped != null)
            {
                this.Append(skipped);
            }

            foreach (string line in lines)
            {
                this.Append(line);
            }

            if (rolledOver != null)
            {
                this.Append(rolledOver);
            }

            if (lines.Count > 0)
            {
                this.EncodingInfo = Localizer.Format("M.Log.Detected", reader.EncodingName);
            }

            if (lines.Count > 0 || rolledOver != null)
            {
                this.LinesAppended?.Invoke();
            }
        }

        private void Append(string text)
        {
            if (this.buffer.Append(new LogLine(text)) && this.lastJumpIndex >= 0)
            {
                // The line "next error" stopped at moved up with the rest, and the next press
                // carries on from it rather than from the line after it.
                this.lastJumpIndex--;
            }

            this.ErrorCount = this.buffer.ErrorCount;
        }

        private void ClearLines()
        {
            this.buffer.Clear();
            this.ErrorCount = 0;
            this.lastJumpIndex = -1;
        }

        private void RebuildVisibleLines()
        {
            this.buffer.Rebuild();
            this.OnLinesRebuilt();
        }

        /// <summary>Brings what goes with the list up to date after it was refilled whole.</summary>
        private void OnLinesRebuilt()
        {
            this.ErrorCount = this.buffer.ErrorCount;
            this.lastJumpIndex = -1;
            this.LinesAppended?.Invoke();
        }

        private void CompileFilter()
        {
            this.filterRegex = null;
            this.FilterInvalid = false;

            // Trimmed once here rather than once per line inside IsVisible, which the plain
            // substring path called for every line in the buffer on every keystroke.
            this.filterNeedle = this.filter.Trim();

            if (!this.useRegex || string.IsNullOrWhiteSpace(this.filter))
            {
                return;
            }

            try
            {
                this.filterRegex = new System.Text.RegularExpressions.Regex(
                    this.filter,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                this.FilterInvalid = true;
            }
        }

        private bool IsVisible(string line)
        {
            if (string.IsNullOrWhiteSpace(this.filter))
            {
                return true;
            }

            if (this.useRegex)
            {
                if (this.filterRegex is null)
                {
                    return false;
                }

                try
                {
                    return this.filterRegex.IsMatch(line);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    return false;
                }
            }

            return line.Contains(this.filterNeedle, StringComparison.OrdinalIgnoreCase);
        }

        private void JumpToNextError()
        {
            int count = this.Lines.Count;
            for (int step = 1; step <= count; step++)
            {
                int index = (this.lastJumpIndex + step) % count;
                if (this.Lines[index].IsError)
                {
                    this.lastJumpIndex = index;
                    this.AutoScroll = false;
                    this.ScrollToRequested?.Invoke(index);
                    return;
                }
            }
        }

        // Events -----------------------------------------------------------------

        private async Task LoadEventsAsync()
        {
            var entry = this.service;
            if (entry is null)
            {
                return;
            }

            this.IsLoadingEvents = true;
            this.EventsStatus = Localizer.Get("M.Log.EventsLoading");

            try
            {
                // Each record is a native read; hundreds of them do not belong on the UI thread.
                var events = await Task.Run(() => EventLogReader.Read(entry.ServiceName, entry.DisplayName)).ConfigureAwait(true);

                this.Events.Clear();
                foreach (var item in events)
                {
                    this.Events.Add(item);
                }

                this.EventsStatus = events.Count == 0
                    ? Localizer.Get("M.Log.NoEvents")
                    : Localizer.Format("M.Log.EventsLoaded", events.Count);
            }
            finally
            {
                this.IsLoadingEvents = false;
            }
        }

        // Shell ------------------------------------------------------------------

        private void OpenExternally()
        {
            if (this.selectedFile is null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(this.selectedFile.Path) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Log.OpenFailed", e.Message);
            }
        }

        private void Reveal()
        {
            if (this.selectedFile is null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{this.selectedFile.Path}\"") { UseShellExecute = true });
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Common.ExplorerFailed", e.Message);
            }
        }
    }
}
