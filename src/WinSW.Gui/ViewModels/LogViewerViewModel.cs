using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Threading;
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

    /// <summary>
    /// Tails the log files a service produces.
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
        private string logDirectory = string.Empty;
        private string filter = string.Empty;
        private string statusMessage = "Select a service to tail its logs.";
        private bool autoScroll = true;
        private bool isPaused;

        public LogViewerViewModel()
        {
            this.timer = new DispatcherTimer { Interval = PollInterval };
            this.timer.Tick += (_, _) => this.Pump();

            this.RescanCommand = new RelayCommand(this.Rescan, () => this.service != null);
            this.ClearCommand = new RelayCommand(() =>
            {
                this.history.Clear();
                this.Lines.Clear();
            });

            this.TogglePauseCommand = new RelayCommand(() => this.IsPaused = !this.IsPaused);
            this.OpenExternallyCommand = new RelayCommand(this.OpenExternally, () => this.selectedFile != null);
            this.RevealCommand = new RelayCommand(this.Reveal, () => this.selectedFile != null);
        }

        public ObservableCollection<LogFileEntry> Files { get; } = new();

        /// <summary>The lines currently shown, after filtering.</summary>
        public ObservableCollection<string> Lines { get; } = new();

        public RelayCommand RescanCommand { get; }

        public RelayCommand ClearCommand { get; }

        public RelayCommand TogglePauseCommand { get; }

        public RelayCommand OpenExternallyCommand { get; }

        public RelayCommand RevealCommand { get; }

        public string ServiceName => this.service?.ServiceName ?? "No service selected";

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

        /// <summary>Case-insensitive substring filter applied to the buffered lines.</summary>
        public string Filter
        {
            get => this.filter;
            set
            {
                if (this.Set(ref this.filter, value))
                {
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

        public string PauseLabel => this.isPaused ? "Resume" : "Pause";

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        // Lifetime --------------------------------------------------------------

        public void Attach(ServiceEntry entry)
        {
            this.service = entry;
            this.Raise(nameof(this.ServiceName));
            this.RescanCommand.RaiseCanExecuteChanged();
            this.Rescan();
        }

        public void Activate()
        {
            if (this.service != null)
            {
                this.timer.Start();
            }
        }

        public void Deactivate() => this.timer.Stop();

        // Discovery -------------------------------------------------------------

        private void Rescan()
        {
            var entry = this.service;
            if (entry?.ConfigPath is null)
            {
                this.StatusMessage = "This service has no configuration file, so its log location is unknown.";
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
                    this.StatusMessage = $"The log directory '{this.LogDirectory}' does not exist yet.";
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
                    ? $"No log files matching '{stem}*' were found in {this.LogDirectory}."
                    : $"{this.Files.Count} log file{(this.Files.Count == 1 ? string.Empty : "s")} in {this.LogDirectory}.";

                this.SelectedFile = this.Files.FirstOrDefault(f => f.Path == previous) ?? this.Files.FirstOrDefault();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                this.StatusMessage = $"Could not read the log directory: {e.Message}";
            }
        }

        private void OpenSelected()
        {
            this.reader?.Dispose();
            this.reader = null;
            this.history.Clear();
            this.Lines.Clear();

            if (this.selectedFile is null)
            {
                this.timer.Stop();
                return;
            }

            this.reader = new LogTailReader(this.selectedFile.Path);
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
                this.Append("── the log file was reset or rolled ──");
            }

            foreach (string line in lines)
            {
                this.Append(line);
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
            }
        }

        private void RebuildVisibleLines()
        {
            this.Lines.Clear();
            foreach (string line in this.history)
            {
                if (this.IsVisible(line))
                {
                    this.Lines.Add(line);
                }
            }
        }

        private bool IsVisible(string line) =>
            string.IsNullOrWhiteSpace(this.filter)
            || line.Contains(this.filter.Trim(), StringComparison.OrdinalIgnoreCase);

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
                this.StatusMessage = $"Could not open the file: {e.Message}";
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
                this.StatusMessage = $"Could not open Explorer: {e.Message}";
            }
        }
    }
}
