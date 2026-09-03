using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;
using WinSW.Gui.Localization;

namespace WinSW.Gui.ViewModels
{
    /// <summary>
    /// Guided creation of a new service: pick the wrapper and the program, describe the
    /// service, choose logging, then write the configuration and install it in one go.
    /// </summary>
    public sealed class WizardViewModel : ObservableObject
    {
        public const int LastStep = 4;

        private int step = 1;
        private string wrapperPath = string.Empty;
        private string targetPath = string.Empty;
        private string arguments = string.Empty;
        private string workingDirectory = string.Empty;
        private string serviceId = string.Empty;
        private string displayName = string.Empty;
        private string description = string.Empty;
        private string startMode = "Automatic";
        private bool delayedAutoStart;
        private string logMode = "roll-by-size";
        private string logPath = string.Empty;
        private string sizeThresholdKb = "10240";
        private string keepFiles = "8";
        private bool restartOnFailure = true;
        private string restartDelay = "10 sec";
        private bool startAfterInstall = true;
        private string statusMessage = string.Empty;
        private bool isBusy;
        private string configPreview = string.Empty;
        private bool brandWrapper;
        private bool useBundledWrapper = BundledWrapper.IsAvailable;
        private string manufacturer = string.Empty;
        private ServiceEntry? cloneSource;
        private bool placeNextToProgram;
        private string suggestedWorkingDirectory = string.Empty;
        private bool desktopTask;
        private string logonDelay = "30 sec";
        private bool runElevated;
        private string keepAliveInterval = "1 min";

        public WizardViewModel()
        {
            this.NextCommand = new RelayCommand(() => this.Step++, () => !this.isBusy && this.step < LastStep && this.CanLeaveCurrentStep());
            this.BackCommand = new RelayCommand(() => this.Step--, () => !this.isBusy && this.step > 1);
            this.DownloadWrapperCommand = new AsyncRelayCommand(this.DownloadWrapperAsync, () => !this.isBusy);
            this.InstallCommand = new AsyncRelayCommand(this.InstallAsync, () => this.step == LastStep && !this.isBusy);
            this.ResetCommand = new RelayCommand(this.Reset);

            this.BrowseWrapperCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile(Localizer.Get("M.Dlg.SelectWrapper"), Localizer.Get("M.Filter.Wrapper")) is { } path)
                {
                    this.UseBundledWrapper = false;
                    this.WrapperPath = path;
                }
            });

            this.BrowseTargetCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile(Localizer.Get("M.Dlg.SelectProgram"), Localizer.Get("M.Filter.Programs")) is { } path)
                {
                    this.TargetPath = path;
                }
            });

            this.BrowseWorkingDirectoryCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Dlg.SelectWorkingDirectory"), this.workingDirectory) is { } path)
                {
                    this.WorkingDirectory = path;
                }
            });

            this.BrowseLogPathCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder(Localizer.Get("M.Dlg.SelectLogDirectory"), this.logPath) is { } path)
                {
                    this.LogPath = path;
                }
            });

            Localizer.Changed += () =>
            {
                this.Raise(nameof(this.StepTitle));
                this.Raise(nameof(this.InstallLabel));
                if (this.step == LastStep)
                {
                    this.RefreshPreview();
                }
            };
        }

        /// <summary>Raised with the new service ID after a successful installation.</summary>
        public event Action<string>? Completed;

        /// <summary>Raised with the new task name after a desktop task has been registered.</summary>
        public event Action<string>? DesktopTaskCompleted;

        /// <summary>
        /// Host the program as a scheduled task in the logged-on session instead of as a
        /// Windows service.
        /// </summary>
        /// <remarks>
        /// A service runs in session 0, which has no desktop; nothing it starts can show a
        /// window or drive the screen. Anything with a user interface — an automation robot
        /// above all — has to run in the session someone is actually logged on to, and a
        /// scheduled task with a logon trigger is how Windows starts a program there.
        /// </remarks>
        public bool DesktopTask
        {
            get => this.desktopTask;
            set
            {
                if (this.Set(ref this.desktopTask, value))
                {
                    this.Raise(nameof(this.IsService));
                    this.Raise(nameof(this.InstallRoot));
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.SharedWrapperPath));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.Raise(nameof(this.IdInUse));
                    this.Raise(nameof(this.InstallLabel));
                    this.RefreshCommands();
                }
            }
        }

        /// <summary>The inverse of <see cref="DesktopTask"/>, for the fields only a service has.</summary>
        public bool IsService => !this.desktopTask;

        /// <summary>The account the task will run as; it is the one registering it.</summary>
        public string TaskAccount => DesktopTaskPlan.CurrentUser;

        /// <summary>How long after logon to wait before starting. A desktop still settling is a bad one to automate.</summary>
        public string LogonDelay
        {
            get => this.logonDelay;
            set => this.Set(ref this.logonDelay, value);
        }

        /// <summary>Run the program with the account's full token, so one that needs administrator rights gets them without a prompt.</summary>
        public bool RunElevated
        {
            get => this.runElevated;
            set => this.Set(ref this.runElevated, value);
        }

        /// <summary>How often the trigger re-fires to bring a program that has died back up.</summary>
        public string KeepAliveInterval
        {
            get => this.keepAliveInterval;
            set => this.Set(ref this.keepAliveInterval, value);
        }

        /// <summary>Desktop tasks the wizard must not collide with; supplied by the shell.</summary>
        public IEnumerable<DesktopTaskEntry> TaskSources { get; set; } = Array.Empty<DesktopTaskEntry>();

        public string InstallLabel => Localizer.Get(this.desktopTask ? "M.Wiz.Register" : "S.Install");

        public AsyncRelayCommand DownloadWrapperCommand { get; }

        /// <summary>
        /// Put the wrapper and configuration in the program's own folder instead of under the
        /// install root. Off by default: a program's folder is often one something else owns —
        /// a Python or JDK installation that an upgrade will replace, taking the service's
        /// configuration and logs with it.
        /// </summary>
        public bool PlaceNextToProgram
        {
            get => this.placeNextToProgram;
            set
            {
                if (this.Set(ref this.placeNextToProgram, value))
                {
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                }
            }
        }

        /// <summary>True when this build carries a wrapper of its own.</summary>
        public bool HasBundledWrapper => BundledWrapper.IsAvailable;

        /// <summary>
        /// Install the wrapper that ships inside this application instead of one the user
        /// supplies. It is written into the program's folder when the service is created, so
        /// nothing has to be downloaded or hunted for first.
        /// </summary>
        public bool UseBundledWrapper
        {
            get => this.useBundledWrapper;
            set
            {
                if (this.Set(ref this.useBundledWrapper, value))
                {
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.RefreshCommands();
                }
            }
        }

        public string BundledWrapperHint => Localizer.Format("M.Wiz.BundledHint", BundledWrapper.Version ?? "3.x");

        /// <summary>
        /// The root holding one folder per service; configurable in the settings. A desktop
        /// task uses the per-user root instead: it runs as one account with no elevation, and
        /// everything under the folder — the configuration and, more to the point, the logs —
        /// has to be writable by that account.
        /// </summary>
        public string InstallRoot => this.desktopTask
            ? AppSettings.Current.EffectiveTaskRoot
            : AppSettings.Current.EffectiveInstallRoot;

        /// <summary>
        /// Where this service's configuration and logs end up: its own folder under the
        /// install root, or the program's folder when that was asked for.
        /// </summary>
        public string InstallDirectory
        {
            get
            {
                if (this.placeNextToProgram)
                {
                    return string.IsNullOrWhiteSpace(this.targetPath)
                        ? string.Empty
                        : Path.GetDirectoryName(this.targetPath) ?? string.Empty;
                }

                if (!this.useBundledWrapper && !string.IsNullOrWhiteSpace(this.wrapperPath))
                {
                    // A wrapper the user supplied keeps its own folder, as it did before.
                    return Path.GetDirectoryName(this.wrapperPath) ?? string.Empty;
                }

                string id = this.serviceId.Trim();
                return id.Length == 0 ? string.Empty : Path.Combine(this.InstallRoot, id);
            }
        }

        /// <summary>The single wrapper every service under the install root runs from.</summary>
        public string SharedWrapperPath => Path.Combine(this.InstallRoot, "bin", "WinSW.exe");

        /// <summary>True when the chosen ID already belongs to an installed service or a registered task.</summary>
        public bool IdInUse
        {
            get
            {
                if (string.IsNullOrWhiteSpace(this.serviceId))
                {
                    return false;
                }

                string id = this.serviceId.Trim();
                return this.desktopTask
                    ? this.TaskSources.Any(t => string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase))
                    : this.Sources.Any(s => string.Equals(s.ServiceName, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        public ObservableCollection<string> Problems { get; } = new();

        public RelayCommand NextCommand { get; }

        public RelayCommand BackCommand { get; }

        public AsyncRelayCommand InstallCommand { get; }

        public RelayCommand ResetCommand { get; }

        public RelayCommand BrowseWrapperCommand { get; }

        public RelayCommand BrowseTargetCommand { get; }

        public RelayCommand BrowseWorkingDirectoryCommand { get; }

        public RelayCommand BrowseLogPathCommand { get; }

        public string[] StartModes => ServiceConfigModel.StartModes;

        /// <summary>Installed services the wizard can start from; supplied by the shell.</summary>
        public IEnumerable<ServiceEntry> Sources { get; set; } = Array.Empty<ServiceEntry>();

        /// <summary>Picking one copies its program, arguments and settings into the wizard.</summary>
        public ServiceEntry? CloneSource
        {
            get => this.cloneSource;
            set
            {
                if (this.Set(ref this.cloneSource, value) && value != null)
                {
                    this.PrefillFrom(value);
                }
            }
        }

        /// <summary>
        /// Copy the wrapper as <c>&lt;service id&gt;.exe</c> with the given company name in its
        /// version information, so the service shows up under its own name in Task Manager.
        /// </summary>
        public bool BrandWrapper
        {
            get => this.brandWrapper;
            set
            {
                if (this.Set(ref this.brandWrapper, value))
                {
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                }
            }
        }

        public string Manufacturer
        {
            get => this.manufacturer;
            set => this.Set(ref this.manufacturer, value);
        }

        /// <summary>The wrapper that will actually be registered: the branded copy, or the original.</summary>
        public string EffectiveWrapperPath
        {
            get
            {
                if (!this.useBundledWrapper && string.IsNullOrWhiteSpace(this.wrapperPath))
                {
                    return string.Empty;
                }

                string directory = this.InstallDirectory;
                if (directory.Length == 0)
                {
                    return string.Empty;
                }

                // Branding needs a copy of its own — the whole point is a wrapper named after
                // the service — so it opts out of sharing.
                if (this.brandWrapper && !string.IsNullOrWhiteSpace(this.serviceId))
                {
                    return Path.Combine(directory, this.serviceId.Trim() + ".exe");
                }

                // One wrapper under the root, shared by every service installed there.
                if (this.useBundledWrapper && !this.placeNextToProgram)
                {
                    return this.SharedWrapperPath;
                }

                return Path.Combine(directory, this.useBundledWrapper ? "WinSW.exe" : Path.GetFileName(this.wrapperPath));
            }
        }

        public string[] LogModes { get; } = { "append", "reset", "roll-by-size", "roll-by-time", "none" };

        public int Step
        {
            get => this.step;
            set
            {
                value = Math.Clamp(value, 1, LastStep);
                if (this.Set(ref this.step, value))
                {
                    if (value == LastStep)
                    {
                        this.RefreshPreview();
                    }

                    this.Raise(nameof(this.StepTitle));
                    this.Raise(nameof(this.IsLastStep));
                    this.RefreshCommands();
                }
            }
        }

        public bool IsLastStep => this.step == LastStep;

        public string StepTitle => Localizer.Get(this.step switch
        {
            1 => "M.Wiz.Step1",
            2 => "M.Wiz.Step2",
            3 => "M.Wiz.Step3",
            _ => "M.Wiz.Step4",
        });

        // Step 1 ---------------------------------------------------------------

        /// <summary>The WinSW executable that will host the service.</summary>
        public string WrapperPath
        {
            get => this.wrapperPath;
            set
            {
                if (this.Set(ref this.wrapperPath, value))
                {
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.RefreshCommands();
                }
            }
        }

        public string TargetPath
        {
            get => this.targetPath;
            set
            {
                if (this.Set(ref this.targetPath, value))
                {
                    this.SuggestDefaults();
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.RefreshCommands();
                }
            }
        }

        public string Arguments
        {
            get => this.arguments;
            set
            {
                if (this.Set(ref this.arguments, value))
                {
                    this.SuggestDefaults();
                }
            }
        }

        public string WorkingDirectory
        {
            get => this.workingDirectory;
            set => this.Set(ref this.workingDirectory, value);
        }

        // Step 2 ---------------------------------------------------------------

        public string ServiceId
        {
            get => this.serviceId;
            set
            {
                if (this.Set(ref this.serviceId, value))
                {
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.Raise(nameof(this.IdInUse));
                    this.RefreshCommands();
                }
            }
        }

        public string DisplayName
        {
            get => this.displayName;
            set => this.Set(ref this.displayName, value);
        }

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

        public bool DelayedAutoStart
        {
            get => this.delayedAutoStart;
            set => this.Set(ref this.delayedAutoStart, value);
        }

        // Step 3 ---------------------------------------------------------------

        public string LogMode
        {
            get => this.logMode;
            set
            {
                if (this.Set(ref this.logMode, value))
                {
                    this.Raise(nameof(this.UsesSizeRolling));
                }
            }
        }

        public bool UsesSizeRolling => this.logMode == "roll-by-size";

        public string LogPath
        {
            get => this.logPath;
            set => this.Set(ref this.logPath, value);
        }

        public string SizeThresholdKb
        {
            get => this.sizeThresholdKb;
            set => this.Set(ref this.sizeThresholdKb, value);
        }

        public string KeepFiles
        {
            get => this.keepFiles;
            set => this.Set(ref this.keepFiles, value);
        }

        public bool RestartOnFailure
        {
            get => this.restartOnFailure;
            set => this.Set(ref this.restartOnFailure, value);
        }

        public string RestartDelay
        {
            get => this.restartDelay;
            set => this.Set(ref this.restartDelay, value);
        }

        // Step 4 ---------------------------------------------------------------

        public bool StartAfterInstall
        {
            get => this.startAfterInstall;
            set => this.Set(ref this.startAfterInstall, value);
        }

        /// <summary>
        /// The configuration is written next to the wrapper, named after the service ID.
        /// That is the layout <c>winsw install</c> expects and the one the dashboard
        /// resolves back from the registry.
        /// </summary>
        public string ConfigPath
        {
            get
            {
                string directory = this.InstallDirectory;
                if (directory.Length == 0 || string.IsNullOrWhiteSpace(this.serviceId))
                {
                    return string.Empty;
                }

                return Path.Combine(directory, this.serviceId + ".xml");
            }
        }

        public string ConfigPreview
        {
            get => this.configPreview;
            private set => this.Set(ref this.configPreview, value);
        }

        public string StatusMessage
        {
            get => this.statusMessage;
            set => this.Set(ref this.statusMessage, value);
        }

        public bool IsBusy
        {
            get => this.isBusy;
            set
            {
                if (this.Set(ref this.isBusy, value))
                {
                    this.RefreshCommands();
                }
            }
        }

        // Behaviour --------------------------------------------------------------

        private void SuggestDefaults()
        {
            if (string.IsNullOrWhiteSpace(this.targetPath))
            {
                return;
            }

            string stem = Path.GetFileNameWithoutExtension(this.targetPath);

            if (string.IsNullOrWhiteSpace(this.serviceId))
            {
                this.ServiceId = stem.Replace(' ', '-');
            }

            if (string.IsNullOrWhiteSpace(this.displayName))
            {
                this.DisplayName = stem;
            }

            // The executable's own folder is the right working directory for a program, and
            // the wrong one for an interpreter: python.exe lives in the Python installation,
            // not beside the script it is being asked to run. When an argument names a file
            // that exists, that file's folder is what the program actually works in.
            string suggestion = ScriptDirectory(this.arguments) ?? Path.GetDirectoryName(this.targetPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(this.workingDirectory)
                || string.Equals(this.workingDirectory, this.suggestedWorkingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                this.suggestedWorkingDirectory = suggestion;
                this.WorkingDirectory = suggestion;
            }

            // Logs in their own folder beside the configuration: %BASE% is wherever the
            // configuration ends up, and the wrapper creates the directory if it is missing.
            if (string.IsNullOrWhiteSpace(this.logPath))
            {
                this.LogPath = @"%BASE%\logs";
            }
        }

        /// <summary>The folder of the first argument that names a file on disk, or null.</summary>
        internal static string? ScriptDirectory(string arguments)
        {
            foreach (string token in ServiceDiscovery.SplitCommandLine(arguments))
            {
                if (token.Length > 2 && !token.StartsWith('-') && !token.StartsWith('/') && File.Exists(token))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(token));
                }
            }

            return null;
        }

        private bool CanLeaveCurrentStep() => this.step switch
        {
            1 => !string.IsNullOrWhiteSpace(this.targetPath) && (this.useBundledWrapper || File.Exists(this.wrapperPath)),
            2 => !string.IsNullOrWhiteSpace(this.serviceId) && !this.IdInUse,
            _ => true,
        };

        /// <summary>
        /// Fetches the wrapper build matching this machine from the latest WinSW release,
        /// into the program's folder when one is chosen, so a first-time user never has to
        /// go looking for WinSW.exe.
        /// </summary>
        private async Task DownloadWrapperAsync()
        {
            string? folder = !string.IsNullOrWhiteSpace(this.targetPath)
                ? Path.GetDirectoryName(this.targetPath)
                : Dialogs.PickFolder(Localizer.Get("M.Dlg.WrapperFolder"));
            if (folder is null)
            {
                return;
            }

            this.IsBusy = true;
            this.StatusMessage = Localizer.Get("M.Wiz.FetchingRelease");
            try
            {
                var latest = await UpdateChecker.LatestWrapperAsync().ConfigureAwait(true);
                string asset = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => "WinSW-arm64.exe",
                    System.Runtime.InteropServices.Architecture.X64 => "WinSW-x64.exe",
                    _ => "WinSW-x86.exe",
                };

                if (latest is null || !latest.Assets.TryGetValue(asset, out string? url))
                {
                    // Older releases only ship x64/x86; fall back to the framework build.
                    if (latest != null && latest.Assets.TryGetValue("WinSW-net461.exe", out url))
                    {
                        asset = "WinSW-net461.exe";
                    }
                    else
                    {
                        this.StatusMessage = Localizer.Get("M.Wiz.ReleaseUnavailable");
                        return;
                    }
                }

                this.StatusMessage = Localizer.Format("M.Dash.Downloading", asset, latest.Version);
                string? downloaded = await UpdateChecker.DownloadAsync(url, folder).ConfigureAwait(true);
                if (downloaded is null)
                {
                    this.StatusMessage = Localizer.Get("M.Dash.DownloadFailed");
                    return;
                }

                string final = Path.Combine(folder, "WinSW.exe");
                if (!string.Equals(downloaded, final, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(downloaded, final, overwrite: true);
                    File.Delete(downloaded);
                }

                this.UseBundledWrapper = false;
                this.WrapperPath = final;
                this.StatusMessage = Localizer.Format("M.Wiz.WrapperDownloaded", latest.Version, final);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", e.Message);
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        public ServiceConfigModel BuildModel()
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = this.serviceId.Trim();
            model.DisplayName = NullIfBlank(this.displayName);
            model.Description = NullIfBlank(this.description);
            model.Executable = this.targetPath.Trim();
            model.Arguments = NullIfBlank(this.arguments);
            model.WorkingDirectory = NullIfBlank(this.workingDirectory);
            model.StartMode = this.startMode;
            model.DelayedAutoStart = this.delayedAutoStart;
            model.LogMode = this.logMode;
            model.LogPath = NullIfBlank(this.logPath);

            if (this.UsesSizeRolling)
            {
                model.SizeThreshold = NullIfBlank(this.sizeThresholdKb);
                model.KeepFiles = NullIfBlank(this.keepFiles);
            }

            if (this.desktopTask)
            {
                // The wrapper allocates a console so that it can send the child a Ctrl+C on
                // stop. In session 0 nobody sees it; in the session the user is logged on to
                // it would be a black window in front of them for as long as the program runs.
                model.HideWindow = true;
            }
            else if (this.restartOnFailure)
            {
                // Recovery actions belong to the service control manager, which never sees a
                // desktop task. Bringing one of those back up is the trigger's job instead.
                model.FailureActions.Add(new FailureAction { Action = "restart", Delay = NullIfBlank(this.restartDelay) ?? "10 sec" });
                model.ResetFailureAfter = "1 hour";
            }

            return model;

            static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private void RefreshPreview()
        {
            var model = this.BuildModel();

            this.Problems.Clear();
            foreach (string problem in model.Validate())
            {
                this.Problems.Add(problem);
            }

            if (File.Exists(this.ConfigPath))
            {
                this.Problems.Add(Localizer.Format("M.Wiz.Exists", this.ConfigPath));
            }

            if (File.Exists(this.wrapperPath) && !ServiceDiscovery.IsWrapperExecutable(this.wrapperPath))
            {
                this.Problems.Add(Localizer.Format("M.Wiz.NotWrapper", this.wrapperPath));
            }

            // Installing the bundled wrapper writes WinSW.exe into the program's folder. If
            // something else already answers to that name, say so before overwriting it.
            if (this.useBundledWrapper
                && this.EffectiveWrapperPath is { Length: > 0 } destination
                && File.Exists(destination)
                && !ServiceDiscovery.IsWrapperExecutable(destination))
            {
                this.Problems.Add(Localizer.Format("M.Wiz.WouldOverwrite", destination));
            }

            if (this.IdInUse)
            {
                this.Problems.Add(Localizer.Format("M.Wiz.IdInUse", this.serviceId.Trim()));
            }

            try
            {
                this.ConfigPreview = model.ToXmlString();
            }
            catch (Exception e)
            {
                this.ConfigPreview = $"<!-- {e.Message} -->";
            }
        }

        private async Task InstallAsync()
        {
            this.RefreshPreview();

            var model = this.BuildModel();
            if (model.Validate().Count > 0)
            {
                this.StatusMessage = Localizer.Get("M.Wiz.FixProblems");
                return;
            }

            this.IsBusy = true;
            try
            {
                string configPath = this.ConfigPath;
                string wrapper = this.EffectiveWrapperPath;

                // The bundled wrapper is unpacked to a per-user cache first, so that from
                // here on it is an ordinary source file like one the user picked.
                string source = this.wrapperPath;
                if (this.useBundledWrapper)
                {
                    this.StatusMessage = Localizer.Get("M.Wiz.Unpacking");
                    if (BundledWrapper.Extract() is not { } unpacked)
                    {
                        this.StatusMessage = Localizer.Get("M.Wiz.UnpackFailed");
                        return;
                    }

                    source = unpacked;
                }

                if (this.brandWrapper)
                {
                    this.StatusMessage = Localizer.Format("M.Wiz.Branding", wrapper);

                    var customized = await WinSwCli.CustomizeAsync(source, wrapper, string.IsNullOrWhiteSpace(this.manufacturer) ? model.DisplayName ?? model.Id : this.manufacturer.Trim()).ConfigureAwait(true);
                    if (!customized.Succeeded)
                    {
                        this.StatusMessage = Localizer.Format("M.Wiz.BrandFailed", customized.Error ?? string.Empty);
                        return;
                    }
                }
                else if (!string.Equals(wrapper, source, StringComparison.OrdinalIgnoreCase)
                    && !ServiceDiscovery.IsWrapperExecutable(wrapper))
                {
                    // A shared wrapper already in place is left alone: another service may be
                    // running from it, which locks the file. Replacing it is a separate action.
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(wrapper)!);
                        File.Copy(source, wrapper, overwrite: true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        var copied = await WinSwCli.CopyElevatedAsync(source, wrapper).ConfigureAwait(true);
                        if (!copied.Succeeded)
                        {
                            this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", copied.Error ?? Localizer.Get("M.Editor.ElevatedSaveDeclined"));
                            return;
                        }
                    }
                    catch (IOException e)
                    {
                        this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", e.Message);
                        return;
                    }
                }

                this.StatusMessage = Localizer.Format("M.Wiz.Writing", configPath);

                if (!await this.WriteConfigurationAsync(model, configPath).ConfigureAwait(true))
                {
                    return;
                }

                if (this.desktopTask)
                {
                    await this.RegisterDesktopTaskAsync(model, wrapper, configPath).ConfigureAwait(true);
                    return;
                }

                // Install and start ride on one elevation prompt; a separate start would
                // mean a second UAC dialog for what the user sees as one action.
                this.StatusMessage = Localizer.Format(this.startAfterInstall ? "M.Wiz.InstallingStarting" : "M.Wiz.Installing", model.Id);
                var result = this.startAfterInstall
                    ? await WinSwCli.InstallAndStartAsync(wrapper, configPath).ConfigureAwait(true)
                    : await WinSwCli.InstallAsync(wrapper, configPath).ConfigureAwait(true);

                if (!result.Succeeded)
                {
                    this.StatusMessage = result.Cancelled
                        ? Localizer.Get("M.Wiz.InstallDeclined")
                        : result.Error ?? Localizer.Get("M.Wiz.InstallFailed");
                    return;
                }

                this.StatusMessage = Localizer.Format(this.startAfterInstall ? "M.Wiz.InstalledStarted" : "M.Wiz.Installed", model.Id);
                this.Completed?.Invoke(model.Id);
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        /// <summary>
        /// Registers the scheduled task that will host this configuration in the logged-on
        /// session, and optionally starts it straight away.
        /// </summary>
        /// <remarks>
        /// Nothing here needs administrator rights: the task runs as the account registering
        /// it, with its own token, and everything it touches is under that account's own
        /// application-data folder. That is the whole reason a desktop task is a per-user
        /// thing rather than a machine-wide one.
        /// </remarks>
        private async Task RegisterDesktopTaskAsync(ServiceConfigModel model, string wrapper, string configPath)
        {
            this.StatusMessage = Localizer.Format("M.Wiz.Registering", model.Id);

            var plan = new DesktopTaskPlan(model.Id, wrapper, configPath)
            {
                Description = model.Description ?? model.DisplayName ?? model.Id,
                RunElevated = this.runElevated,
                LogonDelay = Duration(this.logonDelay, TimeSpan.FromSeconds(30)),
                KeepAlive = this.restartOnFailure,

                // A repetition may not be shorter than a minute; asking for less is asking
                // the task scheduler to reject the whole registration.
                KeepAliveInterval = Max(Duration(this.keepAliveInterval, TimeSpan.FromMinutes(1)), TimeSpan.FromMinutes(1)),
            };

            bool start = this.startAfterInstall;

            try
            {
                await Task.Run(() =>
                {
                    DesktopTasks.Register(plan);
                    if (start)
                    {
                        DesktopTasks.Start(plan.Id);
                    }
                }).ConfigureAwait(true);
            }
            catch (Exception e)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.RegisterFailed", e.Message);
                return;
            }

            this.StatusMessage = Localizer.Format(start ? "M.Wiz.RegisteredStarted" : "M.Wiz.Registered", model.Id);
            this.DesktopTaskCompleted?.Invoke(plan.Id);

            static TimeSpan Max(TimeSpan x, TimeSpan y) => x > y ? x : y;
        }

        private static TimeSpan Duration(string value, TimeSpan fallback) =>
            !string.IsNullOrWhiteSpace(value) && ServiceConfigModel.TryParseTime(value, out var parsed) ? parsed : fallback;

        /// <summary>
        /// Writes next to the wrapper, which is often under Program Files; when that is not
        /// writable for a standard user the file is staged and copied with elevation.
        /// </summary>
        private async Task<bool> WriteConfigurationAsync(ServiceConfigModel model, string configPath)
        {
            try
            {
                // The service's own folder under the install root will not exist yet.
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                model.Save(configPath);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException e)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", e.Message);
                return false;
            }

            string staging = Path.Combine(Path.GetTempPath(), "WinSW.Gui", Path.GetFileName(configPath));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                model.Save(staging);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", e.Message);
                return false;
            }

            var copy = await WinSwCli.CopyElevatedAsync(staging, configPath).ConfigureAwait(true);
            if (copy.Succeeded)
            {
                return true;
            }

            this.StatusMessage = copy.Cancelled
                ? Localizer.Get("M.Editor.ElevatedSaveDeclined")
                : Localizer.Format("M.Wiz.WriteFailed", copy.Error ?? string.Empty);
            return false;
        }

        private void PrefillFrom(ServiceEntry entry)
        {
            if (entry.ConfigPath is null)
            {
                return;
            }

            ServiceConfigModel model;
            try
            {
                model = ServiceConfigModel.Load(entry.ConfigPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.CloneFailed", e.Message);
                return;
            }

            this.UseBundledWrapper = false;
            this.WrapperPath = entry.WrapperPath;
            this.TargetPath = model.Executable;
            this.Arguments = model.Arguments ?? string.Empty;
            this.WorkingDirectory = model.WorkingDirectory ?? string.Empty;
            this.ServiceId = model.Id + "-2";
            this.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id + " (2)" : model.DisplayName + " (2)";
            this.Description = model.Description ?? string.Empty;
            this.StartMode = model.StartMode;
            this.DelayedAutoStart = model.DelayedAutoStart;
            this.LogMode = Array.IndexOf(this.LogModes, model.LogMode) >= 0 ? model.LogMode : "roll-by-size";
            this.LogPath = model.LogPath ?? string.Empty;
            this.SizeThresholdKb = model.SizeThreshold ?? "10240";
            this.KeepFiles = model.KeepFiles ?? "8";
            this.RestartOnFailure = model.FailureActions.Count > 0;
            this.RestartDelay = model.FailureActions.FirstOrDefault()?.Delay ?? "10 sec";
            this.StatusMessage = Localizer.Format("M.Wiz.Cloned", entry.ServiceName);
        }

        private void Reset()
        {
            this.CloneSource = null;
            this.UseBundledWrapper = BundledWrapper.IsAvailable;
            this.PlaceNextToProgram = false;
            this.suggestedWorkingDirectory = string.Empty;
            this.BrandWrapper = false;
            this.Manufacturer = string.Empty;
            this.Step = 1;
            this.WrapperPath = string.Empty;
            this.TargetPath = string.Empty;
            this.Arguments = string.Empty;
            this.WorkingDirectory = string.Empty;
            this.ServiceId = string.Empty;
            this.DisplayName = string.Empty;
            this.Description = string.Empty;
            this.StartMode = "Automatic";
            this.DelayedAutoStart = false;
            this.LogMode = "roll-by-size";
            this.LogPath = string.Empty;
            this.SizeThresholdKb = "10240";
            this.KeepFiles = "8";
            this.RestartOnFailure = true;
            this.RestartDelay = "10 sec";
            this.LogonDelay = "30 sec";
            this.KeepAliveInterval = "1 min";
            this.RunElevated = false;
            this.StartAfterInstall = true;
            this.StatusMessage = string.Empty;
            this.ConfigPreview = string.Empty;
            this.Problems.Clear();
        }

        private void RefreshCommands()
        {
            this.NextCommand.RaiseCanExecuteChanged();
            this.BackCommand.RaiseCanExecuteChanged();
            this.InstallCommand.RaiseCanExecuteChanged();
            this.DownloadWrapperCommand.RaiseCanExecuteChanged();
        }
    }
}
