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
    public sealed class LogFileEntry
    {
        public LogFileEntry(FileInfo file)
        {
            this.Path = file.FullName;
            this.Name = file.Name;
            this.Length = file.Length;
            this.LastWrite = file.LastWriteTime;
        }

        public string Path { get; }

        public string Name { get; }

        public long Length { get; }

        public DateTime LastWrite { get; }

        public string Caption =>
            $"{this.Name}   ·   {FormatSize(this.Length)}   ·   {this.LastWrite:yyyy-MM-dd HH:mm:ss}";

        private static string FormatSize(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        };

        public override string ToString() => this.Caption;
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
    public sealed class LogViewerViewModel : ObservableObject
    {
        private const int MaxLines = 5000;
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(600);

        private readonly DispatcherTimer timer;
        private readonly LinkedList<string> history = new();

        private LogTailReader? reader;
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
        private bool useRegex;
        private bool wrapLines = AppSettings.Current.LogWrapLines;
        private double fontSize = AppSettings.Current.LogFontSize;
        private ServiceEntry? selectedService;
        private System.Text.RegularExpressions.Regex? filterRegex;
        private bool filterInvalid;
        private int errorCount;
        private int lastJumpIndex = -1;

        public LogViewerViewModel()
        {
            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += (_, _) => this.Pump();

            this.Encodings = new[]
            {
                new EncodingOption(LogEncodingChoice.Auto, "M.Enc.Auto"),
                new EncodingOption(LogEncodingChoice.Utf8, "M.Enc.Utf8"),
                new EncodingOption(LogEncodingChoice.SystemAnsi, "M.Enc.Ansi"),
            };
            this.selectedEncoding = this.Encodings.FirstOrDefault(e => e.Choice == AppSettings.Current.LogEncoding) ?? this.Encodings[0];

            this.RescanCommand = new RelayCommand(this.Rescan, () => this.service != null);
            this.ClearCommand = new RelayCommand(() =>
            {
                this.history.Clear();
                this.Lines.Clear();
                this.ErrorCount = 0;
                this.lastJumpIndex = -1;
            });

            this.TogglePauseCommand = new RelayCommand(() => this.IsPaused = !this.IsPaused);
            this.OpenExternallyCommand = new RelayCommand(this.OpenExternally, () => this.selectedFile != null);
            this.RevealCommand = new RelayCommand(this.Reveal, () => this.selectedFile != null);
            this.RefreshEventsCommand = new AsyncRelayCommand(this.LoadEventsAsync, () => this.service != null && !this.isLoadingEvents);
            this.NextErrorCommand = new RelayCommand(this.JumpToNextError, () => this.errorCount > 0);

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

        /// <summary>The lines currently shown, after filtering.</summary>
        public ObservableCollection<string> Lines { get; } = new();

        public ObservableCollection<ServiceEvent> Events { get; } = new();

        public EncodingOption[] Encodings { get; }

        public RelayCommand RescanCommand { get; }

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

        public void Attach(ServiceEntry entry)
        {
            this.service = entry;
            this.selectedService = entry;
            this.Raise(nameof(this.SelectedService));
            this.Raise(nameof(this.ServiceName));
            this.RescanCommand.RaiseCanExecuteChanged();
            this.RefreshEventsCommand.RaiseCanExecuteChanged();
            this.Rescan();
            this.RefreshEventsCommand.Execute(null);
        }

        public void Activate()
        {
            if (this.service != null)
            {
                this.timer.Start();
            }
        }

        public void Deactivate() => this.timer.Stop();

        // Files ------------------------------------------------------------------

        private void Rescan()
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
                this.LogDirectory = ConfigPaths.ResolveLogDirectory(model, entry.ConfigPath);
                string stem = ConfigPaths.ResolveLogBaseName(model, entry.ConfigPath);

                string? previous = this.selectedFile?.Path;
                this.Files.Clear();

                var directory = new DirectoryInfo(this.LogDirectory);
                if (!directory.Exists)
                {
                    this.StatusMessage = Localizer.Format("M.Log.DirMissing", this.LogDirectory);
                    return;
                }

                // Everything the appenders may produce for this service: .out.log, .err.log,
                // .wrapper.log and the numbered or dated files the rolling modes add.
                var files = directory
                    .EnumerateFiles(stem + "*")
                    .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                        || f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                foreach (var file in files)
                {
                    this.Files.Add(new LogFileEntry(file));
                }

                this.StatusMessage = this.Files.Count == 0
                    ? Localizer.Format("M.Log.NoFiles", stem, this.LogDirectory)
                    : Localizer.Format("M.Log.Files", this.Files.Count, this.LogDirectory);

                this.SelectedFile = this.Files.FirstOrDefault(f => f.Path == previous) ?? this.Files.FirstOrDefault();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                this.StatusMessage = Localizer.Format("M.Log.DirFailed", e.Message);
            }
        }

        private void OpenSelected()
        {
            this.reader?.Dispose();
            this.reader = null;
            this.history.Clear();
            this.Lines.Clear();
            this.ErrorCount = 0;
            this.lastJumpIndex = -1;
            this.EncodingInfo = string.Empty;

            if (this.selectedFile is null)
            {
                this.timer.Stop();
                return;
            }

            this.reader = new LogTailReader(this.selectedFile.Path, this.selectedEncoding.Choice);
            this.timer.Start();
            this.Pump();
        }

        private void Pump()
        {
            if (this.reader is null || this.isPaused)
            {
                return;
            }

            var lines = this.reader.ReadNewLines();

            if (this.reader.Restarted)
            {
                this.history.Clear();
                this.Lines.Clear();
                this.Append(Localizer.Get("M.Log.Rolled"));
            }

            foreach (string line in lines)
            {
                this.Append(line);
            }

            if (lines.Count > 0)
            {
                this.EncodingInfo = Localizer.Format("M.Log.Detected", this.reader.EncodingName);
                this.LinesAppended?.Invoke();
            }
        }

        private void Append(string line)
        {
            this.history.AddLast(line);
            if (this.history.Count > MaxLines)
            {
                string dropped = this.history.First!.Value;
                this.history.RemoveFirst();

                // Keep the visible list in step with the buffer, but only when the dropped
                // line was actually on screen.
                if (this.Lines.Count > 0 && this.Lines[0] == dropped)
                {
                    this.Lines.RemoveAt(0);
                }
            }

            if (this.IsVisible(line))
            {
                this.Lines.Add(line);
                if (LogSeverity.IsError(line))
                {
                    this.ErrorCount++;
                }
            }
        }

        private void RebuildVisibleLines()
        {
            this.Lines.Clear();
            int errors = 0;
            foreach (string line in this.history)
            {
                if (this.IsVisible(line))
                {
                    this.Lines.Add(line);
                    if (LogSeverity.IsError(line))
                    {
                        errors++;
                    }
                }
            }

            this.ErrorCount = errors;
            this.lastJumpIndex = -1;
            this.LinesAppended?.Invoke();
        }

        private void CompileFilter()
        {
            this.filterRegex = null;
            this.FilterInvalid = false;

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

            return line.Contains(this.filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void JumpToNextError()
        {
            int count = this.Lines.Count;
            for (int step = 1; step <= count; step++)
            {
                int index = (this.lastJumpIndex + step) % count;
                if (LogSeverity.IsError(this.Lines[index]))
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
