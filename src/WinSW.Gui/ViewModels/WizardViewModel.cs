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

        /// <summary>The roll-by-time pattern the wizard writes: one file a day, named by its date.</summary>
        internal const string DailyRollPattern = "yyyyMMdd";

        /// <summary>
        /// How long a new service has to run without failing before the service control
        /// manager starts its restarts over from the first; the recovery hint says so.
        /// </summary>
        internal const string FailureResetPeriod = "1 hour";

        /// <summary>The waits the restarts grow to when the program keeps failing; see <see cref="RestartDelays"/>.</summary>
        private static readonly string[] LaterRestartDelays = { "1 min", "5 min" };

        private int step = 1;
        private string wrapperPath = string.Empty;

        /// <summary>
        /// Whether <see cref="WrapperPath"/> names a file, read when the path changes rather
        /// than every time Next asks whether it may be enabled. CanExecute runs on every
        /// RaiseCanExecuteChanged, and File.Exists against a share that is not answering is
        /// an SMB timeout on the UI thread for each of them.
        /// </summary>
        private bool wrapperExists;
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
        private string keepDays = "30";
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

        /// <summary>What the program turned out to be, read from disk when it changes; see <see cref="PythonProject"/>.</summary>
        private PythonTarget python = PythonTarget.None;
        private string suggestedServiceId = string.Empty;
        private string suggestedDisplayName = string.Empty;

        /// <summary>
        /// Whether the variables a Python program needs have been filled in for the current
        /// program. They are offered once, when the program becomes a Python one, so that a row
        /// the user removed stays removed while they go on typing.
        /// </summary>
        private bool pythonEnvironmentOffered;

        /// <summary>The rows filled in that way, taken back out if the program stops being Python before they are edited.</summary>
        private readonly List<EnvironmentVariable> offeredVariables = new();

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

            this.AddEnvironmentVariableCommand = new RelayCommand(() =>
                this.EnvironmentVariables.Add(new EnvironmentVariable { Name = "NAME", Value = string.Empty }));
            this.RemoveEnvironmentVariableCommand = new RelayCommand(p =>
            {
                if (p is EnvironmentVariable variable)
                {
                    this.EnvironmentVariables.Remove(variable);
                }
            });

            Localizer.Changed += () =>
            {
                this.Raise(nameof(this.StepTitle));
                this.Raise(nameof(this.InstallLabel));
                this.Raise(nameof(this.PythonHint));
                this.Raise(nameof(this.RecoveryHint));
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

        /// <summary>
        /// Things on the review step that will probably go wrong but do not stop the install:
        /// a path may be right on the machine the service is really meant for.
        /// </summary>
        public ObservableCollection<string> Warnings { get; } = new();

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
                    this.wrapperExists = File.Exists(value);
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
                    this.python = PythonProject.Inspect(value);
                    this.Raise(nameof(this.TargetIsJar));
                    this.Raise(nameof(this.TargetIsPython));
                    this.Raise(nameof(this.TargetIsPythonScript));
                    this.Raise(nameof(this.PythonInterpreterMissing));
                    this.Raise(nameof(this.PythonHint));
                    this.SuggestDefaults();
                    this.OfferPythonEnvironment();
                    this.Raise(nameof(this.InstallDirectory));
                    this.Raise(nameof(this.ConfigPath));
                    this.Raise(nameof(this.EffectiveWrapperPath));
                    this.RefreshCommands();
                }
            }
        }

        /// <summary>The program is a .jar, which is run through java; see <see cref="BuildModel"/>.</summary>
        public bool TargetIsJar => IsJar(this.targetPath);

        /// <summary>Python runs the program, so the variables it needs under a service are offered.</summary>
        public bool TargetIsPython => this.python.IsPython;

        /// <summary>The program is a .py script, which is run through an interpreter; see <see cref="BuildModel"/>.</summary>
        public bool TargetIsPythonScript => this.python.IsScript;

        /// <summary>A script was picked and there is no Python a service could run it with.</summary>
        public bool PythonInterpreterMissing => this.python.InterpreterSource == PythonInterpreterSource.NotFound;

        /// <summary>Which Python will run the picked script, or why none will.</summary>
        public string PythonHint => this.python.InterpreterSource switch
        {
            PythonInterpreterSource.VirtualEnvironment => Localizer.Format("M.Wiz.PyHintVenv", this.python.Interpreter),
            PythonInterpreterSource.SystemPath => Localizer.Format("M.Wiz.PyHintPath", this.python.Interpreter),
            PythonInterpreterSource.NotFound => Localizer.Get("M.Wiz.PyHintMissing"),
            _ => string.Empty,
        };

        /// <summary>The <c>&lt;env&gt;</c> entries the service starts with; filled in for a Python program.</summary>
        public ObservableCollection<EnvironmentVariable> EnvironmentVariables { get; } = new();

        public RelayCommand AddEnvironmentVariableCommand { get; }

        public RelayCommand RemoveEnvironmentVariableCommand { get; }

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
                    this.Raise(nameof(this.UsesTimeRolling));
                }
            }
        }

        public bool UsesSizeRolling => this.logMode == "roll-by-size";

        /// <summary>One file a day; only how many days to keep is asked. See <see cref="BuildModel"/>.</summary>
        public bool UsesTimeRolling => this.logMode == "roll-by-time";

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

        /// <summary>
        /// How many daily files roll-by-time keeps. A field of its own rather than
        /// <see cref="KeepFiles"/>: a month of days and eight files of 10 MB are different
        /// defaults, and switching the mode back and forth must not trade one for the other.
        /// </summary>
        public string KeepDays
        {
            get => this.keepDays;
            set => this.Set(ref this.keepDays, value);
        }

        public bool RestartOnFailure
        {
            get => this.restartOnFailure;
            set => this.Set(ref this.restartOnFailure, value);
        }

        public string RestartDelay
        {
            get => this.restartDelay;
            set
            {
                if (this.Set(ref this.restartDelay, value))
                {
                    this.Raise(nameof(this.RecoveryHint));
                }
            }
        }

        /// <summary>The waits before each restart, and that the last one repeats; see <see cref="RestartDelays"/>.</summary>
        public string RecoveryHint =>
            Localizer.Format("M.Wiz.RecoveryHint", string.Join(" → ", RestartDelays(this.restartDelay)));

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

            // A program in a virtual environment's Scripts folder is a launcher named after the
            // tool — uvicorn, python — and a script is as often as not called main.py; neither
            // names the service. The folder the environment was made in, the project, does.
            string? root = this.python.ProjectRoot;
            string name = ProjectName(root) ?? Path.GetFileNameWithoutExtension(this.targetPath);

            // Both follow the program for as long as they still hold what was suggested: a
            // path typed a character at a time would otherwise leave the ID its first letter.
            if (string.IsNullOrWhiteSpace(this.serviceId) || string.Equals(this.serviceId, this.suggestedServiceId, StringComparison.Ordinal))
            {
                this.suggestedServiceId = name.Replace(' ', '-');
                this.ServiceId = this.suggestedServiceId;
            }

            if (string.IsNullOrWhiteSpace(this.displayName) || string.Equals(this.displayName, this.suggestedDisplayName, StringComparison.Ordinal))
            {
                this.suggestedDisplayName = name;
                this.DisplayName = name;
            }

            // The executable's own folder is the right working directory for a program, and
            // the wrong one for an interpreter or a launcher: python.exe lives in the Python
            // installation and uvicorn.exe in the environment's Scripts folder, neither beside
            // the code they are asked to run. What the arguments name comes first, then the
            // project the environment belongs to. A script picked as the program works in its
            // own folder, the way a .jar does.
            string suggestion = this.python.IsScript
                ? Path.GetDirectoryName(this.targetPath) ?? string.Empty
                : ScriptDirectory(this.arguments, root) ?? root ?? Path.GetDirectoryName(this.targetPath) ?? string.Empty;
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

        /// <summary>
        /// The folder the first argument naming something on disk works in: a file given by
        /// its full path works in its own folder; a file or a <c>module:app</c> given relative
        /// to <paramref name="projectRoot"/> works in the project. Null when nothing is named.
        /// </summary>
        /// <remarks>
        /// A relative argument is never looked up from here: this console's current directory
        /// has nothing to do with the one the service will start in. Relative to the project,
        /// <c>app\main.py</c> needs the project as its working directory, not <c>app</c>.
        /// </remarks>
        internal static string? ScriptDirectory(string arguments, string? projectRoot = null)
        {
            bool module = false;
            foreach (string token in ServiceDiscovery.SplitCommandLine(arguments))
            {
                // What follows python's -m is a module name rather than a path.
                bool afterModuleSwitch = module;
                module = token == "-m";

                if (token.Length == 0 || token.StartsWith('-') || token.StartsWith('/'))
                {
                    continue;
                }

                if (projectRoot != null && PythonProject.NamesEntryUnder(token, projectRoot, afterModuleSwitch))
                {
                    return projectRoot;
                }

                if (token.Length > 2 && Path.IsPathRooted(token) && File.Exists(token))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(token));
                }
            }

            return null;
        }

        /// <summary>The name of a project folder, or null for none or for a drive's root.</summary>
        private static string? ProjectName(string? root) =>
            root is null || Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is not { Length: > 0 } name ? null : name;

        /// <summary>
        /// Fills in the variables a Python program needs under a service when the program
        /// becomes a Python one, and takes them back out, if nobody has touched them, when it
        /// stops being one. Rows already there under the same name are left as they are.
        /// </summary>
        private void OfferPythonEnvironment()
        {
            if (this.python.IsPython == this.pythonEnvironmentOffered)
            {
                return;
            }

            this.pythonEnvironmentOffered = this.python.IsPython;
            if (this.python.IsPython)
            {
                foreach (var (name, value) in PythonProject.RecommendedEnvironment)
                {
                    if (!this.EnvironmentVariables.Any(v => string.Equals(v.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                    {
                        var variable = new EnvironmentVariable { Name = name, Value = value };
                        this.offeredVariables.Add(variable);
                        this.EnvironmentVariables.Add(variable);
                    }
                }

                return;
            }

            foreach (var variable in this.offeredVariables)
            {
                if (PythonProject.RecommendedEnvironment.Contains((variable.Name, variable.Value)))
                {
                    this.EnvironmentVariables.Remove(variable);
                }
            }

            this.offeredVariables.Clear();
        }

        internal static bool IsJar(string path) =>
            string.Equals(Path.GetExtension(path.Trim()), ".jar", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// What java is given for a .jar: <c>-jar "file"</c> ahead of the arguments. Arguments
        /// that already say <c>-jar</c> are a Java command line written out in full — JVM
        /// options in front of it, most likely — and are handed over as they are.
        /// </summary>
        internal static string JarArguments(string jarPath, string arguments)
        {
            string rest = arguments.Trim();
            if (ServiceDiscovery.SplitCommandLine(rest).Contains("-jar"))
            {
                return rest;
            }

            string jar = "-jar \"" + jarPath.Trim() + "\"";
            return rest.Length == 0 ? jar : jar + " " + rest;
        }

        /// <summary>
        /// The waits before each restart of a new service: the one chosen, then a minute, then
        /// five minutes. The service control manager repeats the last action for every failure
        /// after that, so a lone ten-second restart would start a program that can never run
        /// some 360 times an hour; this comes to about twelve. A later wait no longer than the
        /// chosen one is left out, so the waits only ever grow.
        /// </summary>
        /// <remarks>
        /// A blank delay is the ten seconds the wizard starts with. One that does not parse is
        /// kept as it is, for validation to point at, with the full ladder after it.
        /// </remarks>
        internal static string[] RestartDelays(string? chosen)
        {
            string first = string.IsNullOrWhiteSpace(chosen) ? "10 sec" : chosen.Trim();
            if (!ServiceConfigModel.TryParseTime(first, out var firstWait))
            {
                return LaterRestartDelays.Prepend(first).ToArray();
            }

            return LaterRestartDelays
                .Where(later => ServiceConfigModel.TryParseTime(later, out var wait) && wait > firstWait)
                .Prepend(first)
                .ToArray();
        }

        private bool CanLeaveCurrentStep() => this.step switch
        {
            1 => !string.IsNullOrWhiteSpace(this.targetPath) && (this.useBundledWrapper || this.wrapperExists),
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

                // The download may have landed on the path already chosen, in which case the
                // setter saw no change and did not re-read; the file exists now either way.
                this.wrapperExists = File.Exists(final);
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
            string target = this.targetPath.Trim();
            if (IsJar(target))
            {
                // The wrapper starts its program the way CreateProcess does, and a .jar is not
                // something that can be started that way: written as the executable, it is a
                // service that installs cleanly and then fails every start. It is java that
                // runs, with the file handed to it. The bare name, as upstream's samples use
                // it, is looked up on the PATH the service sees, which is the system's.
                model.Executable = "java";
                model.Arguments = JarArguments(target, this.arguments);
            }
            else if (this.python.IsScript)
            {
                // A script cannot be started that way either. It is run by the python.exe of
                // the virtual environment beside it, where its packages are; failing that, by
                // one on the machine's PATH, written out in full because the PATH a service
                // sees is whatever services.exe read at boot.
                model.Executable = this.python.Interpreter ?? "python";
                model.Arguments = PythonProject.ScriptArguments(target, this.arguments);
            }
            else
            {
                model.Executable = target;
                model.Arguments = NullIfBlank(this.arguments);
            }

            foreach (var variable in this.EnvironmentVariables)
            {
                model.EnvironmentVariables.Add(new EnvironmentVariable { Name = variable.Name.Trim(), Value = variable.Value });
            }

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
            else if (this.UsesTimeRolling)
            {
                // The wrapper refuses roll-by-time without a pattern, and without keepFiles it
                // keeps every file it ever wrote. A daily pattern makes the count a count of
                // days; the period is left at the wrapper's default of one.
                model.RollPattern = DailyRollPattern;
                model.KeepFiles = NullIfBlank(this.keepDays);
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
                foreach (string delay in RestartDelays(this.restartDelay))
                {
                    model.FailureActions.Add(new FailureAction { Action = "restart", Delay = delay });
                }

                model.ResetFailureAfter = FailureResetPeriod;
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

            this.Warnings.Clear();
            if (this.WorkingDirectoryWarning(model) is { } warning)
            {
                this.Warnings.Add(warning);
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

        /// <summary>
        /// A working directory that is part of Python rather than of the application: a
        /// Scripts folder, an installation or a virtual environment. An application started
        /// there cannot import its own modules, and the service fails and is restarted, over and
        /// over. Read the way the wrapper resolves it, so that leaving the field empty with the
        /// configuration placed in a Scripts folder is caught too.
        /// </summary>
        private string? WorkingDirectoryWarning(ServiceConfigModel model)
        {
            string configPath = this.ConfigPath;
            string directory;
            try
            {
                if (configPath.Length > 0)
                {
                    directory = ConfigPaths.ResolveWorkingDirectory(model, configPath);
                }
                else if (!string.IsNullOrWhiteSpace(model.WorkingDirectory))
                {
                    directory = Environment.ExpandEnvironmentVariables(model.WorkingDirectory!);
                }
                else
                {
                    return null;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                // A path that cannot even be resolved is no Python folder; there is nothing to
                // say about it here.
                return null;
            }

            var folder = PythonProject.Classify(directory, out string? projectRoot);
            return (folder, projectRoot) switch
            {
                (PythonFolder.VirtualEnvironmentScripts, { } root) => Localizer.Format("M.Wiz.WorkDirVenvScripts", directory, root),
                (PythonFolder.VirtualEnvironment, { } root) => Localizer.Format("M.Wiz.WorkDirVenv", directory, root),
                (PythonFolder.VirtualEnvironmentScripts or PythonFolder.InstallationScripts, _) => Localizer.Format("M.Wiz.WorkDirScripts", directory),
                (PythonFolder.VirtualEnvironment or PythonFolder.Installation, _) => Localizer.Format("M.Wiz.WorkDirPython", directory),
                _ => null,
            };
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
                ActionLog.Record(this.startAfterInstall ? "install + start" : "install", model.Id, result);

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
                ActionLog.Record(start ? "register task + start" : "register task", model.Id, "failed: " + e.Message);
                return;
            }

            ActionLog.Record(start ? "register task + start" : "register task", model.Id, "ok");
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
                // The service's own folder under the install root will not exist yet. When it
                // does, and holds a configuration, the wizard is about to overwrite it.
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                ConfigHistory.Preserve(configPath);
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

            CommandResult copy;
            try
            {
                copy = await StagedWrite.ElevatedAsync(model, configPath).ConfigureAwait(true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.StatusMessage = Localizer.Format("M.Wiz.WriteFailed", e.Message);
                return false;
            }

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

            // A time-rolled source's count is a count of its files, which is a count of days
            // once the wizard writes its daily pattern in place of whatever the source had.
            bool byTime = model.LogMode == "roll-by-time";
            this.KeepFiles = (byTime ? null : model.KeepFiles) ?? "8";
            this.KeepDays = (byTime ? model.KeepFiles : null) ?? "30";
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
            this.suggestedServiceId = string.Empty;
            this.suggestedDisplayName = string.Empty;
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
            this.KeepDays = "30";
            this.RestartOnFailure = true;
            this.RestartDelay = "10 sec";
            this.LogonDelay = "30 sec";
            this.KeepAliveInterval = "1 min";
            this.RunElevated = false;
            this.StartAfterInstall = true;
            this.StatusMessage = string.Empty;
            this.ConfigPreview = string.Empty;
            this.Problems.Clear();
            this.Warnings.Clear();

            // Emptying the program above took back only the offered rows nobody had edited.
            this.EnvironmentVariables.Clear();
            this.offeredVariables.Clear();
            this.pythonEnvironmentOffered = false;
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
