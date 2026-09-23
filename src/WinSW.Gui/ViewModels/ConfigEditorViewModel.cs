using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;
using WinSW.Gui.Localization;

namespace WinSW.Gui.ViewModels
{
    /// <summary>One line of the editor's History menu: a version to compare and restore, or a note.</summary>
    public sealed class HistoryEntry
    {
        public HistoryEntry(string label, ConfigVersion? version, bool restorable = false)
        {
            this.Label = label;
            this.Version = version;
            this.CanRestore = version != null && restorable;
        }

        public string Label { get; }

        public ConfigVersion? Version { get; }

        /// <summary>Any version can be compared; looking changes nothing.</summary>
        public bool CanCompare => this.Version != null;

        /// <summary>Only when the form holds nothing unsaved for the restore to replace.</summary>
        public bool CanRestore { get; }
    }

    /// <summary>
    /// Graphical editor over a WinSW configuration file, with live validation and a preview
    /// of exactly what will be written.
    /// </summary>
    public sealed class ConfigEditorViewModel : ObservableObject, IDisposable
    {
        /// <summary>
        /// The preview and validation are recomputed on a short idle delay rather than on
        /// every keystroke: serialising the document per character would be wasteful and
        /// would make typing feel heavy.
        /// </summary>
        private static readonly TimeSpan RecomputeDelay = TimeSpan.FromMilliseconds(350);

        private readonly DispatcherTimer recomputeTimer;
        private ServiceConfigModel model = ServiceConfigModel.CreateNew();
        private ServiceEntry? installedService;
        private string? filePath;
        private string xmlPreview = string.Empty;
        private string statusMessage = string.Empty;
        private bool isDirty;
        private bool showPreview = true;
        private bool isXmlEditing;
        private string xmlEditorText = string.Empty;
        private int recomputeGeneration;
        private readonly TrialRunner trial = new();
        private readonly List<string> pendingTrialLines = new();
        private bool trialFlushScheduled;
        private bool isTrialRunning;
        private string trialStatus = string.Empty;
        private string proxyTestTarget = ProxyProbe.DefaultTarget;
        private string proxyTestStatus = string.Empty;
        private bool proxyTestFailed;

        public ConfigEditorViewModel()
        {
            this.recomputeTimer = new DispatcherTimer { Interval = RecomputeDelay };
            this.recomputeTimer.Tick += (_, _) =>
            {
                this.recomputeTimer.Stop();
                this.Recompute();
            };

            this.SaveCommand = new AsyncRelayCommand(this.SaveAsync, () => this.filePath != null);
            this.SaveAsCommand = new AsyncRelayCommand(this.SaveAsAsync);
            this.OpenCommand = new RelayCommand(this.Open);
            this.ReloadCommand = new RelayCommand(this.Reload, () => this.filePath != null && File.Exists(this.filePath));
            this.RestoreVersionCommand = new RelayCommand(p => this.Restore((p as HistoryEntry)?.Version), p => p is HistoryEntry { CanRestore: true });
            this.ApplyToServiceCommand = new AsyncRelayCommand(this.ApplyToServiceAsync, () => this.installedService != null && this.filePath != null);
            this.InstallCommand = new AsyncRelayCommand(this.InstallAsync, () => this.installedService is null && this.filePath != null);

            this.BrowseExecutableCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile(Localizer.Get("M.Dlg.SelectExecutable"), Localizer.Get("M.Filter.Executables")) is { } path)
                {
                    this.Model.Executable = this.Relativize(path);

                    // The wrapper's default working directory is its own folder, which is
                    // rarely what a program expects; the program's folder is the usual intent.
                    if (string.IsNullOrWhiteSpace(this.Model.WorkingDirectory))
                    {
                        this.Model.WorkingDirectory = this.Relativize(Path.GetDirectoryName(path) ?? string.Empty);
                    }
                }
            });

            this.OpenHelpCommand = new RelayCommand(p =>
            {
                string anchor = p as string ?? string.Empty;
                SystemShell.OpenUrl(ProjectLinks.Doc("xml-config-file.md", anchor));
            });

            this.BrowseStopExecutableCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile(Localizer.Get("M.Dlg.SelectStopExecutable"), Localizer.Get("M.Filter.Executables")) is { } path)
                {
                    this.Model.StopExecutable = path;
                }
            });

            this.BrowseWorkingDirectoryCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Dlg.SelectWorkingDirectory")) is { } path)
                {
                    this.Model.WorkingDirectory = path;
                }
            });

            this.BrowseLogPathCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Dlg.SelectLogDirectory")) is { } path)
                {
                    this.Model.LogPath = path;
                }
            });

            this.AddEnvironmentVariableCommand = new RelayCommand(() =>
                this.Model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "NAME", Value = string.Empty }));
            this.RemoveEnvironmentVariableCommand = new RelayCommand(
                p => Remove(this.Model.EnvironmentVariables, p as EnvironmentVariable));

            this.AddDownloadCommand = new RelayCommand(() =>
                this.Model.Downloads.Add(new DownloadItem { From = "https://", To = "%BASE%\\file" }));
            this.RemoveDownloadCommand = new RelayCommand(
                p => Remove(this.Model.Downloads, p as DownloadItem));

            this.AddFailureActionCommand = new RelayCommand(() =>
                this.Model.FailureActions.Add(new FailureAction()));
            this.RemoveFailureActionCommand = new RelayCommand(
                p => Remove(this.Model.FailureActions, p as FailureAction));

            this.AddDependencyCommand = new RelayCommand(() =>
                this.Model.Dependencies.Add(new DependencyItem()));
            this.RemoveDependencyCommand = new RelayCommand(
                p => Remove(this.Model.Dependencies, p as DependencyItem));

            this.AddMappingCommand = new RelayCommand(() =>
                this.Model.SharedDirectories.Add(new DriveMapping()));
            this.RemoveMappingCommand = new RelayCommand(
                p => Remove(this.Model.SharedDirectories, p as DriveMapping));
            this.ClearHookCommand = new RelayCommand(p => (p as ProcessCommandModel)?.Clear());

            this.EnterXmlEditCommand = new RelayCommand(() =>
            {
                this.Recompute();
                this.XmlEditorText = this.XmlPreview;
                this.IsXmlEditing = true;
            });
            this.ApplyXmlCommand = new RelayCommand(this.ApplyXml);
            this.CancelXmlEditCommand = new RelayCommand(() => this.IsXmlEditing = false);

            this.StartTrialCommand = new RelayCommand(this.StartTrial, () => !this.isTrialRunning);
            this.TestProxyCommand = new AsyncRelayCommand(this.TestProxyAsync);
            this.StopTrialCommand = new RelayCommand(() => this.trial.Stop(), () => this.isTrialRunning);
            this.ClearTrialCommand = new RelayCommand(() => this.TrialOutput.Clear());

            this.trial.Output += (line, isError) => this.QueueTrialLine(isError ? "[stderr] " + line : line);
            this.trial.Exited += code =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    // Whatever is still queued goes in ahead of the exit line.
                    this.FlushTrialLines();
                    this.AppendTrial(Localizer.Format("M.Trial.Exited", code));
                    this.TrialStatus = Localizer.Format("M.Trial.Exited", code);
                    this.IsTrialRunning = false;
                });

            this.Attach(this.model);

            Localizer.Changed += () =>
            {
                this.Raise(nameof(this.FileLabel));
                this.Recompute();
            };

            static void Remove<T>(ObservableCollection<T> collection, T? item)
            {
                if (item != null)
                {
                    collection.Remove(item);
                }
            }
        }

        public ServiceConfigModel Model
        {
            get => this.model;
            private set => this.Set(ref this.model, value);
        }

        public ObservableCollection<string> Problems { get; } = new();

        public AsyncRelayCommand SaveCommand { get; }

        public AsyncRelayCommand SaveAsCommand { get; }

        public RelayCommand OpenCommand { get; }

        public RelayCommand ReloadCommand { get; }

        /// <summary>Loads an earlier version of the file into the form; see <see cref="ConfigHistory"/>.</summary>
        public RelayCommand RestoreVersionCommand { get; }

        /// <summary>The History menu's lines, filled by <see cref="RefreshHistory"/> as the menu opens.</summary>
        public ObservableCollection<HistoryEntry> History { get; } = new();

        public AsyncRelayCommand ApplyToServiceCommand { get; }

        public AsyncRelayCommand InstallCommand { get; }

        public RelayCommand BrowseExecutableCommand { get; }

        public RelayCommand OpenHelpCommand { get; }

        /// <summary>Raised after a save or apply, for a transient on-screen notice.</summary>
        public event Action<string, bool>? Toast;

        /// <summary>Raised with the service ID after the editor installs a configuration.</summary>
        public event Action<string>? ServiceInstalled;

        /// <summary>Raised with the path after the configuration has been written to disk.</summary>
        public event Action<string>? Saved;

        /// <summary>
        /// Paths inside the configuration's own folder are written as %BASE%-relative, so the
        /// service keeps working when the folder is moved or the package is exported.
        /// </summary>
        private string Relativize(string path)
        {
            if (this.filePath is null || path.Length == 0)
            {
                return path;
            }

            string baseDirectory = Path.GetDirectoryName(this.filePath) ?? string.Empty;
            if (baseDirectory.Length == 0)
            {
                return path;
            }

            string full = Path.GetFullPath(path);
            if (string.Equals(full, baseDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return "%BASE%";
            }

            string prefix = baseDirectory.TrimEnd('\\') + "\\";
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? "%BASE%\\" + full.Substring(prefix.Length) : path;
        }

        public RelayCommand BrowseStopExecutableCommand { get; }

        public RelayCommand BrowseWorkingDirectoryCommand { get; }

        public RelayCommand BrowseLogPathCommand { get; }

        public RelayCommand AddEnvironmentVariableCommand { get; }

        public RelayCommand RemoveEnvironmentVariableCommand { get; }

        public RelayCommand AddDownloadCommand { get; }

        public RelayCommand RemoveDownloadCommand { get; }

        public RelayCommand AddFailureActionCommand { get; }

        public RelayCommand RemoveFailureActionCommand { get; }

        public RelayCommand AddDependencyCommand { get; }

        public RelayCommand RemoveDependencyCommand { get; }

        public RelayCommand AddMappingCommand { get; }

        public RelayCommand RemoveMappingCommand { get; }

        public RelayCommand ClearHookCommand { get; }

        public RelayCommand EnterXmlEditCommand { get; }

        public RelayCommand ApplyXmlCommand { get; }

        public RelayCommand CancelXmlEditCommand { get; }

        public RelayCommand StartTrialCommand { get; }

        public RelayCommand StopTrialCommand { get; }

        public RelayCommand ClearTrialCommand { get; }

        public AsyncRelayCommand TestProxyCommand { get; }

        /// <summary>Machine-specific findings from <see cref="ServiceConfigModel.ValidateEnvironment"/>; never block saving.</summary>
        public ObservableCollection<string> Warnings { get; } = new();

        public bool HasWarnings => this.Warnings.Count > 0;

        public ObservableCollection<string> TrialOutput { get; } = new();

        // Raw XML mode ---------------------------------------------------------

        /// <summary>True while the preview pane is an editor instead of a read-only rendering.</summary>
        public bool IsXmlEditing
        {
            get => this.isXmlEditing;
            private set => this.Set(ref this.isXmlEditing, value);
        }

        public string XmlEditorText
        {
            get => this.xmlEditorText;
            set => this.Set(ref this.xmlEditorText, value);
        }

        // Trial run --------------------------------------------------------------

        public bool IsTrialRunning
        {
            get => this.isTrialRunning;
            private set
            {
                if (this.Set(ref this.isTrialRunning, value))
                {
                    this.StartTrialCommand.RaiseCanExecuteChanged();
                    this.StopTrialCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string TrialStatus
        {
            get => this.trialStatus;
            private set => this.Set(ref this.trialStatus, value);
        }

        // Proxy check ------------------------------------------------------------

        /// <summary>
        /// What the probe asks for through the proxy. Not part of the configuration: it is a
        /// question about the network, and the answer differs by what you are trying to reach.
        /// </summary>
        public string ProxyTestTarget
        {
            get => this.proxyTestTarget;
            set => this.Set(ref this.proxyTestTarget, value);
        }

        public string ProxyTestStatus
        {
            get => this.proxyTestStatus;
            private set => this.Set(ref this.proxyTestStatus, value);
        }

        /// <summary>Colours the result; a probe that got nowhere should not read as good news.</summary>
        public bool ProxyTestFailed
        {
            get => this.proxyTestFailed;
            private set => this.Set(ref this.proxyTestFailed, value);
        }

        public string[] StartModes => ServiceConfigModel.StartModes;

        public string[] Priorities => ServiceConfigModel.Priorities;

        public string[] LogModes => ServiceConfigModel.LogModes;

        public string[] AuthTypes { get; } = { "none", "sspi", "basic" };

        public string[] FailureActionTypes { get; } = { "restart", "reboot", "none" };

        public string? FilePath
        {
            get => this.filePath;
            private set
            {
                if (this.Set(ref this.filePath, value))
                {
                    this.Raise(nameof(this.FileLabel));
                    this.SaveCommand.RaiseCanExecuteChanged();
                    this.ReloadCommand.RaiseCanExecuteChanged();
                    this.ApplyToServiceCommand.RaiseCanExecuteChanged();
                    this.InstallCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string FileLabel => this.filePath ?? Localizer.Get("M.Editor.Unsaved");

        /// <summary>Set when the configuration being edited belongs to an installed service.</summary>
        public ServiceEntry? InstalledService
        {
            get => this.installedService;
            private set
            {
                if (this.Set(ref this.installedService, value))
                {
                    this.Raise(nameof(this.IsInstalled));
                    this.ApplyToServiceCommand.RaiseCanExecuteChanged();
                    this.InstallCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsInstalled => this.installedService != null;

        public string XmlPreview
        {
            get => this.xmlPreview;
            private set => this.Set(ref this.xmlPreview, value);
        }

        public bool ShowPreview
        {
            get => this.showPreview;
            set => this.Set(ref this.showPreview, value);
        }

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        public bool IsDirty
        {
            get => this.isDirty;
            private set => this.Set(ref this.isDirty, value);
        }

        public bool HasProblems => this.Problems.Count > 0;

        // Loading --------------------------------------------------------------

        public void LoadFrom(ServiceEntry entry)
        {
            if (entry.ConfigPath is null)
            {
                this.StatusMessage = Localizer.Format("M.Editor.NoConfig", entry.ServiceName);
                return;
            }

            this.Load(entry.ConfigPath);
            this.InstalledService = entry;
        }

        public void Load(string path)
        {
            try
            {
                var loaded = ServiceConfigModel.Load(path);
                this.Detach(this.Model);
                this.Attach(loaded);
                this.Model = loaded;
                this.FilePath = loaded.FilePath;
                this.InstalledService = null;
                this.IsDirty = false;
                this.StatusMessage = Localizer.Format("M.Editor.Loaded", Path.GetFileName(path));
                this.Recompute();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Editor.OpenFailed", Path.GetFileName(path), e.Message);
            }
        }

        public void NewConfiguration()
        {
            var fresh = ServiceConfigModel.CreateNew();
            this.Detach(this.Model);
            this.Attach(fresh);
            this.Model = fresh;
            this.FilePath = null;
            this.InstalledService = null;
            this.IsDirty = false;
            this.StatusMessage = Localizer.Get("M.Editor.New");
            this.Recompute();
        }

        public void Open()
        {
            if (Dialogs.PickFile(Localizer.Get("M.Dlg.OpenConfig"), Localizer.Get("M.Filter.Config")) is { } path)
            {
                this.Load(path);
            }
        }

        private void Reload()
        {
            if (this.filePath != null)
            {
                this.Load(this.filePath);
            }
        }

        /// <summary>
        /// Fills the History menu. Read as it opens rather than kept up to date: it is a folder
        /// of twenty files at most, looked at only when somebody asks.
        /// </summary>
        /// <remarks>
        /// Every version can be compared with the file. Restoring replaces what is in the form,
        /// so it is not offered over unsaved changes: they would be lost without a word, which
        /// is the thing the history is there to stop. The menu says so at its head.
        /// </remarks>
        public void RefreshHistory()
        {
            this.History.Clear();

            if (this.filePath is null)
            {
                this.History.Add(new HistoryEntry(Localizer.Get("M.History.NotSaved"), null));
                return;
            }

            var versions = ConfigHistory.List(this.filePath);
            if (versions.Count == 0)
            {
                this.History.Add(new HistoryEntry(Localizer.Get("M.History.None"), null));
                return;
            }

            if (this.IsDirty)
            {
                this.History.Add(new HistoryEntry(Localizer.Get("M.History.SaveFirst"), null));
            }

            foreach (var version in versions)
            {
                this.History.Add(new HistoryEntry(version.Label, version, restorable: !this.IsDirty));
            }
        }

        /// <summary>
        /// An earlier version against the file as it is on disk — not the form, which may hold
        /// edits nobody has saved. Null when either cannot be read; the status line says why.
        /// </summary>
        public IReadOnlyList<DiffLine>? CompareWithFile(ConfigVersion version)
        {
            if (this.filePath is null)
            {
                return null;
            }

            try
            {
                string earlier = File.ReadAllText(version.Path);
                string current = File.Exists(this.filePath) ? File.ReadAllText(this.filePath) : string.Empty;
                return TextDiff.Compare(earlier, current);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Diff.ReadFailed", e.Message);
                return null;
            }
        }

        /// <summary>
        /// Puts an earlier version into the form, unsaved. Saving it is what restores it — through
        /// the same path as any other save, elevation included, and setting aside the version it
        /// replaces, so a restore can itself be undone.
        /// </summary>
        private void Restore(ConfigVersion? version)
        {
            if (version is null || this.filePath is null || this.IsDirty)
            {
                return;
            }

            try
            {
                var loaded = ServiceConfigModel.Load(version.Path);

                // A version of this file, going back to it: relative paths resolve against
                // where it belongs, not against the history folder it was read from.
                loaded.FilePath = this.filePath;

                this.Detach(this.Model);
                this.Attach(loaded);
                this.Model = loaded;
                this.IsDirty = true;
                this.StatusMessage = Localizer.Format("M.History.Restored", version.SavedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
                this.Recompute();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.History.RestoreFailed", e.Message);
            }
        }

        // Saving ---------------------------------------------------------------

        /// <summary>
        /// Saves, and says whether the file is now on disk. The exit prompt needs the answer:
        /// a save that was refused — an invalid configuration, a declined elevation, a
        /// cancelled Save As — must leave the window open rather than close over the changes.
        /// </summary>
        public async Task<bool> TrySaveAsync()
        {
            await this.SaveAsync().ConfigureAwait(true);
            return !this.IsDirty;
        }

        /// <summary>
        /// Ends a trial run that is still going.
        /// </summary>
        /// <remarks>
        /// A trial run is an ordinary child process, and Windows does not end one because its
        /// parent did. Closing the console while a program was under trial left it running,
        /// with no window and nothing left on screen that knew about it — the stray process
        /// this application exists to prevent.
        /// <para>
        /// This covers closing, not crashing. Surviving the console being killed outright
        /// would need the child in a job object, which is a great deal of machinery for the
        /// rarer half of the problem.
        /// </para>
        /// </remarks>
        public void Dispose()
        {
            this.recomputeTimer.Stop();
            this.trial.Dispose();
        }

        private async Task SaveAsync()
        {
            if (this.filePath is null)
            {
                await this.SaveAsAsync().ConfigureAwait(true);
                return;
            }

            await this.WriteAsync(this.filePath).ConfigureAwait(true);
        }

        private async Task SaveAsAsync()
        {
            string suggested = string.IsNullOrWhiteSpace(this.Model.Id) ? "myapp.xml" : this.Model.Id + ".xml";
            if (Dialogs.PickSaveFile(Localizer.Get("M.Dlg.SaveConfig"), Localizer.Get("M.Filter.ConfigSave"), suggested) is { } path)
            {
                await this.WriteAsync(path).ConfigureAwait(true);
            }
        }

        private async Task WriteAsync(string path)
        {
            this.Recompute();
            if (this.Problems.Count > 0)
            {
                this.StatusMessage = Localizer.Get("M.Editor.FixProblems");
                return;
            }

            // Whatever is on disk now is about to be replaced, whichever way the write goes.
            ConfigHistory.Preserve(path);

            try
            {
                this.Model.Save(path);
                ActionLog.Record("save", path, "ok");
                this.FilePath = this.Model.FilePath;
                this.IsDirty = false;
                this.StatusMessage = this.installedService is null
                    ? Localizer.Format("M.Editor.Saved", path)
                    : Localizer.Format("M.Editor.SavedApply", path);
                this.Toast?.Invoke(Localizer.Format("M.Editor.Saved", Path.GetFileName(path)), false);
                this.Saved?.Invoke(path);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // The configuration usually sits next to the service binary, under Program
                // Files or similar, where a standard user has no write access. Fall through
                // to writing a copy and moving it into place with administrator rights.
            }
            catch (IOException e)
            {
                this.StatusMessage = Localizer.Format("M.Editor.WriteFailed", path, e.Message);
                ActionLog.Record("save", path, "failed: " + e.Message);
                return;
            }

            CommandResult result;
            try
            {
                result = await StagedWrite.ElevatedAsync(this.Model, path).ConfigureAwait(true);
                ActionLog.Record("save (elevated)", path, result);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Editor.WriteFailed", path, e.Message);
                return;
            }

            if (result.Succeeded)
            {
                // Which service this configuration belongs to has to be caught before the
                // reload, because Load clears it. Read afterwards, as it was, the field was
                // always null and the association was silently dropped: an elevated save left
                // the editor offering to install a service that was already installed.
                var installed = this.installedService;

                // Re-read from the real location so the model's backing document is what is on
                // disk rather than what was sent there.
                this.Load(path);
                this.InstalledService = installed;

                this.StatusMessage = Localizer.Format("M.Editor.SavedElevated", path);
                this.Saved?.Invoke(path);
            }
            else
            {
                this.StatusMessage = result.Cancelled
                    ? Localizer.Get("M.Editor.ElevatedSaveDeclined")
                    : result.Error ?? Localizer.Format("M.Editor.WriteFailed", path, string.Empty);
            }
        }

        private async Task ApplyToServiceAsync()
        {
            var entry = this.installedService;
            if (entry is null || this.filePath is null)
            {
                return;
            }

            if (this.IsDirty)
            {
                await this.WriteAsync(this.filePath).ConfigureAwait(true);
                if (this.IsDirty)
                {
                    return;
                }
            }

            this.StatusMessage = Localizer.Format("M.Editor.Applying", entry.ServiceName);
            var result = await WinSwCli.RefreshAsync(entry.WrapperPath, this.filePath).ConfigureAwait(true);
            ActionLog.Record("refresh", entry.ServiceName, result);

            this.StatusMessage = result switch
            {
                { Cancelled: true } => Localizer.Get("M.Editor.ElevationDeclined"),
                { Succeeded: true } => Localizer.Format("M.Editor.Applied", entry.ServiceName),
                _ => result.Error ?? Localizer.Get("M.Editor.RefreshFailed"),
            };
            this.Toast?.Invoke(this.StatusMessage, !result.Succeeded);
        }

        /// <summary>
        /// Installs the configuration being edited as a service, which is the step that was
        /// missing between a successful try run and a running service.
        /// </summary>
        private async Task InstallAsync()
        {
            if (this.filePath is null || this.installedService != null)
            {
                return;
            }

            if (this.IsDirty)
            {
                await this.WriteAsync(this.filePath).ConfigureAwait(true);
                if (this.IsDirty)
                {
                    return;
                }
            }

            if (this.Model.Validate().Count > 0)
            {
                this.StatusMessage = Localizer.Get("M.Wiz.FixProblems");
                return;
            }

            if (await this.ResolveWrapperAsync(Path.GetDirectoryName(this.filePath)!).ConfigureAwait(true) is not { } wrapper)
            {
                this.Toast?.Invoke(this.StatusMessage, true);
                return;
            }

            // A configuration that says it starts by itself is started now; Manual and
            // Disabled are left alone, which is what choosing them asks for.
            bool start = string.IsNullOrWhiteSpace(this.Model.StartMode)
                || string.Equals(this.Model.StartMode, "Automatic", StringComparison.OrdinalIgnoreCase);

            this.StatusMessage = Localizer.Format(start ? "M.Editor.InstallingStarting" : "M.Editor.Installing", this.Model.Id);

            var result = start
                ? await WinSwCli.InstallAndStartAsync(wrapper, this.filePath).ConfigureAwait(true)
                : await WinSwCli.InstallAsync(wrapper, this.filePath).ConfigureAwait(true);
            ActionLog.Record(start ? "install + start" : "install", this.Model.Id, result);

            if (!result.Succeeded)
            {
                this.StatusMessage = result.Cancelled
                    ? Localizer.Get("M.Editor.ElevationDeclined")
                    : result.Error ?? Localizer.Get("M.Editor.InstallFailed");
                this.Toast?.Invoke(this.StatusMessage, true);
                return;
            }

            this.StatusMessage = Localizer.Format(start ? "M.Editor.InstalledStarted" : "M.Editor.Installed", this.Model.Id);
            this.Toast?.Invoke(this.StatusMessage, false);

            // The header's actions change once the configuration belongs to a service, and
            // the dashboard is a list that has just gained an entry. The sweep that finds it
            // touches every service on the machine, which is why the dashboard keeps it off
            // this thread; here it was running on it, at the moment the success toast was
            // meant to appear.
            string installedId = this.Model.Id;
            var found = await Task.Run(ServiceDiscovery.Discover).ConfigureAwait(true);
            this.InstalledService = found.FirstOrDefault(e => string.Equals(e.ServiceName, installedId, StringComparison.OrdinalIgnoreCase));
            this.ServiceInstalled?.Invoke(installedId);
        }

        /// <summary>
        /// The wrapper to install with: one already sitting beside the configuration, or the
        /// one this application carries, unpacked there.
        /// </summary>
        private async Task<string?> ResolveWrapperAsync(string directory)
        {
            string shared = Path.Combine(AppSettings.Current.EffectiveInstallRoot, "bin", "WinSW.exe");

            foreach (string existing in new[] { Path.Combine(directory, this.Model.Id + ".exe"), Path.Combine(directory, "WinSW.exe"), shared })
            {
                if (ServiceDiscovery.IsWrapperExecutable(existing))
                {
                    return existing;
                }
            }

            if (BundledWrapper.Extract() is not { } source)
            {
                this.StatusMessage = Localizer.Get("M.Wiz.UnpackFailed");
                return null;
            }

            // Nothing to reuse: create the shared one, where the wizard puts it too.
            string destination = shared;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            try
            {
                File.Copy(source, destination, overwrite: true);
                return destination;
            }
            catch (UnauthorizedAccessException)
            {
                // Program Files and the like: stage it in with one elevation prompt.
                var copied = await WinSwCli.CopyElevatedAsync(source, destination).ConfigureAwait(true);
                if (copied.Succeeded)
                {
                    return destination;
                }

                this.StatusMessage = copied.Cancelled
                    ? Localizer.Get("M.Editor.ElevatedSaveDeclined")
                    : copied.Error ?? Localizer.Format("M.Editor.WriteFailed", destination, string.Empty);
                return null;
            }
            catch (IOException e)
            {
                this.StatusMessage = Localizer.Format("M.Editor.WriteFailed", destination, e.Message);
                return null;
            }
        }

        // Change tracking --------------------------------------------------------

        private void Attach(ServiceConfigModel target)
        {
            target.PropertyChanged += this.OnModelChanged;

            Watch(target.EnvironmentVariables);
            Watch(target.Downloads);
            Watch(target.FailureActions);
            Watch(target.Dependencies);
            Watch(target.SharedDirectories);

            target.Prestart.PropertyChanged += this.OnModelChanged;
            target.Poststart.PropertyChanged += this.OnModelChanged;
            target.Prestop.PropertyChanged += this.OnModelChanged;
            target.Poststop.PropertyChanged += this.OnModelChanged;

            void Watch<T>(ObservableCollection<T> collection)
                where T : ObservableObject
            {
                collection.CollectionChanged += this.OnCollectionChanged;
                foreach (var item in collection)
                {
                    item.PropertyChanged += this.OnModelChanged;
                }
            }
        }

        /// <summary>
        /// Undoes <see cref="Attach"/>, so a model the editor has moved off stops driving it.
        /// </summary>
        /// <remarks>
        /// Opening a second configuration used to leave the first one wired to this editor.
        /// The subscription runs model to editor, so the editor was never held alive by it and
        /// nothing leaked — but a discarded model that something else still touched would have
        /// restarted the debounce and recomputed the preview of the document now on screen.
        /// </remarks>
        private void Detach(ServiceConfigModel target)
        {
            target.PropertyChanged -= this.OnModelChanged;

            Unwatch(target.EnvironmentVariables);
            Unwatch(target.Downloads);
            Unwatch(target.FailureActions);
            Unwatch(target.Dependencies);
            Unwatch(target.SharedDirectories);

            target.Prestart.PropertyChanged -= this.OnModelChanged;
            target.Poststart.PropertyChanged -= this.OnModelChanged;
            target.Prestop.PropertyChanged -= this.OnModelChanged;
            target.Poststop.PropertyChanged -= this.OnModelChanged;

            void Unwatch<T>(ObservableCollection<T> collection)
                where T : ObservableObject
            {
                collection.CollectionChanged -= this.OnCollectionChanged;
                foreach (var item in collection)
                {
                    item.PropertyChanged -= this.OnModelChanged;
                }
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Rows are edited in place, so each new row needs its own subscription for the
            // preview to follow what is typed into it.
            foreach (var item in e.NewItems?.OfType<ObservableObject>() ?? Enumerable.Empty<ObservableObject>())
            {
                item.PropertyChanged += this.OnModelChanged;
            }

            foreach (var item in e.OldItems?.OfType<ObservableObject>() ?? Enumerable.Empty<ObservableObject>())
            {
                item.PropertyChanged -= this.OnModelChanged;
            }

            this.OnModelChanged(sender, new PropertyChangedEventArgs(null));
        }

        private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ServiceConfigModel.FilePath))
            {
                return;
            }

            this.IsDirty = true;
            this.recomputeTimer.Stop();
            this.recomputeTimer.Start();
        }

        private void Recompute()
        {
            this.Problems.Clear();
            foreach (string problem in this.Model.Validate())
            {
                this.Problems.Add(problem);
            }

            this.Raise(nameof(this.HasProblems));

            try
            {
                this.XmlPreview = this.Model.ToXmlString();
            }
            catch (Exception e)
            {
                this.XmlPreview = Localizer.Format("M.Editor.RenderFailed", e.Message);
            }

            // Account lookups can stall on an unreachable domain; keep them off the UI thread
            // and discard results that a later edit has made stale.
            int generation = ++this.recomputeGeneration;
            var model = this.Model;
            _ = Task.Run(() => model.ValidateEnvironment()).ContinueWith(
                task =>
                {
                    if (generation != this.recomputeGeneration || task.IsFaulted)
                    {
                        return;
                    }

                    this.Warnings.Clear();
                    foreach (string warning in task.Result)
                    {
                        this.Warnings.Add(warning);
                    }

                    this.Raise(nameof(this.HasWarnings));
                },
                System.Threading.CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ApplyXml()
        {
            try
            {
                var replacement = ServiceConfigModel.FromXml(this.xmlEditorText, this.filePath);
                this.Detach(this.Model);
                this.Attach(replacement);
                this.Model = replacement;
                this.IsDirty = true;
                this.IsXmlEditing = false;
                this.StatusMessage = Localizer.Get("M.Editor.XmlApplied");
                this.Recompute();
            }
            catch (InvalidDataException e)
            {
                this.StatusMessage = Localizer.Format("M.Editor.XmlInvalid", e.Message);
            }
        }

        /// <summary>
        /// Asks the configured proxy for the target, and says which of the two ways it can go
        /// wrong actually happened: a proxy nothing answers at, or one that answers and cannot
        /// deliver. The probe runs as this user, which the hint beside the button spells out —
        /// a service usually runs as another.
        /// </summary>
        private async Task TestProxyAsync()
        {
            string? address = this.Model.ProxyAddress;
            if (!string.IsNullOrWhiteSpace(address))
            {
                // %BASE% and the rest reach the wrapper expanded, so they reach the probe
                // expanded too, or a templated address would fail here and nowhere else.
                address = this.filePath != null
                    ? ConfigPaths.Expand(address!, this.filePath)
                    : Environment.ExpandEnvironmentVariables(address!);
            }

            this.ProxyTestFailed = false;
            this.ProxyTestStatus = Localizer.Get("M.Proxy.Testing");

            // A button whose only job is to report what went wrong is the one place a blanket
            // catch belongs: there is nothing it could throw that the user would rather have
            // as a crash than as a line of text.
            try
            {
                var result = await ProxyProbe.RunAsync(address, this.proxyTestTarget);

                this.ProxyTestFailed = result.Outcome != ProxyProbeOutcome.Ok;
                this.ProxyTestStatus = result.Outcome switch
                {
                    ProxyProbeOutcome.Ok => Localizer.Format("M.Proxy.TestOk", result.Target, result.Status, result.Elapsed),
                    ProxyProbeOutcome.NoAddress => Localizer.Get("M.Proxy.TestNoAddress"),
                    ProxyProbeOutcome.BadAddress => Localizer.Format("M.Proxy.TestBadAddress", result.Detail),
                    ProxyProbeOutcome.BadTarget => Localizer.Format("M.Proxy.TestBadTarget", result.Target),
                    ProxyProbeOutcome.ProxyUnreachable => Localizer.Format("M.Proxy.TestUnreachable", result.Endpoint, result.Detail),
                    _ => Localizer.Format("M.Proxy.TestRefused", result.Endpoint, result.Target, result.Detail),
                };
            }
            catch (Exception e)
            {
                this.ProxyTestFailed = true;
                this.ProxyTestStatus = e.Message;
            }
        }

        private void StartTrial()
        {
            this.Recompute();
            try
            {
                this.TrialOutput.Clear();
                this.trial.Start(this.Model, this.filePath);
                this.IsTrialRunning = true;
                this.TrialStatus = Localizer.Format("M.Trial.Running", this.trial.ProcessId);
                this.AppendTrial(Localizer.Format("M.Trial.Started", this.Model.Executable, this.Model.StartArguments ?? this.Model.Arguments ?? string.Empty));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                this.TrialStatus = Localizer.Format("M.Trial.Failed", e.Message);
                this.AppendTrial(this.TrialStatus);
                this.IsTrialRunning = false;
            }
        }

        /// <summary>
        /// Takes one line of trial output off the process's reader thread and schedules a
        /// flush if one is not already waiting.
        /// </summary>
        /// <remarks>
        /// One dispatcher callback per line, which is what this was, makes a program that
        /// prints in a tight loop a program that floods the UI thread: thousands of queued
        /// callbacks, each adding one row and each followed by a layout pass. Lines are
        /// gathered here and added in a batch instead, inside a single callback, so layout
        /// runs once per flush however many lines arrived. Not a Reset — that would rebuild
        /// three thousand rows — just many Adds with no layout between them.
        /// </remarks>
        private void QueueTrialLine(string line)
        {
            bool schedule;
            lock (this.pendingTrialLines)
            {
                this.pendingTrialLines.Add(line);
                schedule = !this.trialFlushScheduled;
                this.trialFlushScheduled = true;
            }

            if (!schedule)
            {
                return;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // No UI to show it on. Drop the batch rather than leave the flag set forever.
                lock (this.pendingTrialLines)
                {
                    this.pendingTrialLines.Clear();
                    this.trialFlushScheduled = false;
                }

                return;
            }

            dispatcher.BeginInvoke(this.FlushTrialLines);
        }

        private void FlushTrialLines()
        {
            string[] lines;
            lock (this.pendingTrialLines)
            {
                lines = this.pendingTrialLines.ToArray();
                this.pendingTrialLines.Clear();
                this.trialFlushScheduled = false;
            }

            foreach (string line in lines)
            {
                this.AppendTrial(line);
            }
        }

        private void AppendTrial(string line)
        {
            const int maxLines = 3000;
            this.TrialOutput.Add(line);
            if (this.TrialOutput.Count > maxLines)
            {
                this.TrialOutput.RemoveAt(0);
            }
        }
    }
}
