using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>The log modes step 3 has fields for.</summary>
        private static readonly string[] OfferedLogModes = { "append", "reset", "roll-by-size", "roll-by-time", "none" };

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

        /// <summary>Set in the constructor, which knows whether this machine can run it; see <see cref="PrefersBundledWrapper"/>.</summary>
        private bool useBundledWrapper;

        /// <summary>
        /// The file the Download button last fetched into the per-user cache; see
        /// <see cref="WrapperIsDownloaded"/>. Remembered by its path, so that picking or typing
        /// another wrapper stops it counting without anything having to be cleared.
        /// </summary>
        private string? downloadedWrapper;
        private string manufacturer = string.Empty;
        private ServiceEntry? cloneSource;

        /// <summary>The configuration of the service being copied, when there is one; see <see cref="PrefillFrom"/>.</summary>
        private ServiceClone? clone;

        /// <summary>
        /// The ID and display name a copy was suggested under, and what they were made from; see
        /// <see cref="RenumberCopy"/>. Null when nothing has been copied.
        /// </summary>
        private CopyNames? copyNames;

        /// <summary>
        /// The delay the copied recovery was shown with. While <see cref="RestartDelay"/> still
        /// holds it, the source's own failure actions are written; see <see cref="UsesSourceRecovery"/>.
        /// </summary>
        private string? cloneRestartDelay;
        private string[] logModes = OfferedLogModes;
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

        /// <summary>This machine's .NET Framework, which decides whether the bundled wrapper can run here at all.</summary>
        private readonly NetFrameworkInfo framework;

        /// <summary>Reads every service on the machine; called off the UI thread. See <see cref="CheckMachineAsync"/>.</summary>
        private readonly Func<ServiceNames> readServices;

        /// <summary>
        /// Every service on the machine by both its names, as last read: when step 2 or the
        /// review step opened, and again right before installing. Replaced whole, never changed.
        /// </summary>
        private ServiceNames machineServices = ServiceNames.None;

        /// <summary>Counts the machine checks started, so that one overtaken by a later one drops its results.</summary>
        private int machineCheckGeneration;

        /// <summary>The review step's name clashes as they were added to <see cref="Problems"/>, to be swapped when a newer read differs.</summary>
        private string[] shownNameClashes = Array.Empty<string>();
        private IReadOnlyList<string> environmentWarnings = Array.Empty<string>();

        public WizardViewModel()
            : this(NetFramework.Installed, ServiceNames.Read)
        {
        }

        /// <summary>
        /// With what the machine has handed in rather than read, so that a test can say what
        /// framework and which services it has.
        /// </summary>
        internal WizardViewModel(NetFrameworkInfo framework, Func<ServiceNames> readServices)
        {
            this.framework = framework;
            this.readServices = readServices;
            this.useBundledWrapper = PrefersBundledWrapper(BundledWrapper.IsAvailable, framework);
            this.Warnings.CollectionChanged += (_, _) => this.Raise(nameof(this.HasWarnings));

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
                this.Raise(nameof(this.RollPatternHint));
                this.Raise(nameof(this.FrameworkHint));
                this.Raise(nameof(this.IdInUseHint));
                this.Raise(nameof(this.DisplayNameInUseHint));
                if (this.step == LastStep)
                {
                    // The machine's findings are worded as they are read; read them again.
                    this.RefreshPreview();
                    this.CheckMachine(environment: true);
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
                    this.RaiseNameChecks();
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
        /// This machine's .NET Framework is older than the 4.6.2 the bundled wrapper needs, so
        /// the self-contained download is the default instead; see <see cref="NetFramework"/>.
        /// </summary>
        public bool FrameworkTooOld => this.framework.TooOldForWrapper;

        /// <summary>What <see cref="FrameworkTooOld"/> means, and the two ways out of it.</summary>
        public string FrameworkHint => this.FrameworkTooOld
            ? Localizer.Format("M.Wiz.NetFxTooOld", this.framework.Version, NetFramework.OfflineInstaller, NetFramework.OfflineInstallerLink)
            : string.Empty;

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

                if (!this.UsesSharedLayout && !string.IsNullOrWhiteSpace(this.wrapperPath))
                {
                    // A wrapper the user keeps in a folder of their own keeps that folder, as it
                    // did before.
                    return Path.GetDirectoryName(this.wrapperPath) ?? string.Empty;
                }

                string id = this.serviceId.Trim();
                return id.Length == 0 ? string.Empty : Path.Combine(this.InstallRoot, id);
            }
        }

        /// <summary>The single wrapper every service under the install root runs from.</summary>
        public string SharedWrapperPath => Path.Combine(this.InstallRoot, "bin", "WinSW.exe");

        /// <summary>
        /// The wrapper is the one the Download button fetched, which sits in a per-user cache
        /// rather than anywhere a service should run from; see <see cref="WrapperDownload"/>.
        /// </summary>
        private bool WrapperIsDownloaded => SamePath(this.wrapperPath, this.downloadedWrapper);

        /// <summary>
        /// The service gets the install root's layout — a folder of its own, and the wrapper
        /// shared from <c>bin</c> — rather than the folder of a wrapper the user keeps: for the
        /// wrapper this application carries, for one it downloaded, and for the shared one
        /// itself, picked for a second service, which would otherwise put that service's files
        /// in <c>bin</c>.
        /// </summary>
        private bool UsesSharedLayout =>
            this.useBundledWrapper || this.WrapperIsDownloaded || SamePath(this.wrapperPath, this.SharedWrapperPath);

        /// <summary>
        /// True when the chosen ID already belongs to a registered task or, for a service, is
        /// the name of any service on the machine, WinSW's or not; see <see cref="ServiceNames"/>.
        /// </summary>
        public bool IdInUse => !string.IsNullOrWhiteSpace(this.serviceId) && this.InUse(this.serviceId.Trim());

        /// <summary>
        /// True when another service on the machine already goes by the chosen display name,
        /// which Windows refuses at install with 1078. A desktop task has no display name to clash.
        /// </summary>
        public bool DisplayNameInUse => this.DisplayNameClash != null;

        /// <summary>Under the ID on step 2: which service already has it.</summary>
        public string IdInUseHint
        {
            get
            {
                if (this.desktopTask)
                {
                    return this.IdInUse ? Localizer.Get("M.Wiz.IdInUseHint") : string.Empty;
                }

                return this.IdClash is { } service ? Localizer.Format("M.Wiz.NameTakenHint", service.Label) : string.Empty;
            }
        }

        /// <summary>Under the display name on step 2: which service already shows it.</summary>
        public string DisplayNameInUseHint =>
            this.DisplayNameClash is { } service ? Localizer.Format("M.Wiz.NameTakenHint", service.Label) : string.Empty;

        public ObservableCollection<string> Problems { get; } = new();

        /// <summary>
        /// Things on the review step that will probably go wrong but do not stop the install:
        /// a path may be right on the machine the service is really meant for.
        /// </summary>
        public ObservableCollection<string> Warnings { get; } = new();

        /// <summary>
        /// What checking the configuration against this machine found on the review step — see
        /// <see cref="ServiceConfigModel.ValidateEnvironment"/> — shown with <see cref="Warnings"/>.
        /// </summary>
        /// <remarks>
        /// Read off the UI thread and arriving a moment after the step opens, so it is kept
        /// apart from the lists filled in as the step opens, and replaced whole rather than
        /// changed: nothing that reads those lists can see them change under it.
        /// </remarks>
        public IReadOnlyList<string> EnvironmentWarnings
        {
            get => this.environmentWarnings;
            private set
            {
                if (this.Set(ref this.environmentWarnings, value))
                {
                    this.Raise(nameof(this.HasWarnings));
                }
            }
        }

        public bool HasWarnings => this.Warnings.Count > 0 || this.environmentWarnings.Count > 0;

        /// <summary>
        /// The machine check last started, for a test to wait on; see <see cref="CheckMachineAsync"/>.
        /// </summary>
        internal Task MachineCheck { get; private set; } = Task.CompletedTask;

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

        /// <summary>
        /// Picking one starts the new service from its whole configuration; see <see cref="PrefillFrom"/>.
        /// </summary>
        /// <remarks>
        /// Null never drops what was copied. The picker writes null back when the service it
        /// shows leaves the list — uninstalled, say, under the wizard — and the fields filled
        /// in from it are still there. Only <see cref="ResetCommand"/> starts over.
        /// </remarks>
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
                bool shared = this.UsesSharedLayout;
                if (shared && !this.placeNextToProgram)
                {
                    return this.SharedWrapperPath;
                }

                // A download is cached under its release name, WinSW-x64.exe say; installed, it
                // is WinSW.exe like the bundled one.
                return Path.Combine(directory, shared ? "WinSW.exe" : Path.GetFileName(this.wrapperPath));
            }
        }

        /// <summary>
        /// The wizard's own log modes, and a copied service's when it used another: a copy of
        /// a <c>roll-by-size-time</c> service stays one, with the settings it came with.
        /// </summary>
        public string[] LogModes
        {
            get => this.logModes;
            private set => this.Set(ref this.logModes, value);
        }

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
                        // Whatever the machine said was about the configuration as it was last
                        // time; it may have changed on the way back here.
                        this.EnvironmentWarnings = Array.Empty<string>();
                        this.RefreshPreview();
                    }

                    // The names are read where they are asked for, the ID and display name on
                    // step 2, and read again for the review: a service installed in the
                    // meantime, by anyone, takes its names with it.
                    if (value == 2 || value == LastStep)
                    {
                        this.CheckMachine(environment: value == LastStep);
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
                    this.RaiseNameChecks();
                    this.RefreshCommands();
                }
            }
        }

        public string DisplayName
        {
            get => this.displayName;
            set
            {
                if (this.Set(ref this.displayName, value))
                {
                    this.RaiseNameChecks();
                    this.RefreshCommands();
                }
            }
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
                // The picker writes null back when its list is swapped for one without the
                // mode it showed, as a copy's list can be. No mode is ever meant as blank.
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (this.Set(ref this.logMode, value))
                {
                    this.Raise(nameof(this.UsesSizeRolling));
                    this.Raise(nameof(this.UsesTimeRolling));
                    this.Raise(nameof(this.KeepsSourceRollPattern));
                }
            }
        }

        public bool UsesSizeRolling => this.logMode == "roll-by-size";

        /// <summary>One file a day; only how many days to keep is asked. See <see cref="BuildModel"/>.</summary>
        public bool UsesTimeRolling => this.logMode == "roll-by-time";

        /// <summary>
        /// A copy rolls by time on the pattern its source had rather than on the daily one,
        /// which need not be daily; the count is then a count of files.
        /// </summary>
        public bool KeepsSourceRollPattern => this.UsesTimeRolling && this.clone?.RollPattern != null;

        /// <summary>What <see cref="KeepsSourceRollPattern"/> means for the count, in place of the daily hint.</summary>
        public string RollPatternHint => this.clone?.RollPattern is { } pattern
            ? Localizer.Format("M.Wiz.RollPatternCopied", this.clone.SourceName, pattern)
            : string.Empty;

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

        /// <summary>
        /// The waits before each restart, and that the last one repeats; see
        /// <see cref="RestartDelays"/>. For a copy still on its source's actions, those.
        /// </summary>
        public string RecoveryHint => this.UsesSourceRecovery
            ? Localizer.Format(
                "M.Wiz.RecoveryHintCopied",
                this.clone!.SourceName,
                string.Join(" → ", this.clone.Recovery.Select(DescribeFailureAction)),
                this.clone.ResetFailureAfter ?? ServiceClone.WrapperResetPeriod)
            : Localizer.Format("M.Wiz.RecoveryHint", string.Join(" → ", RestartDelays(this.restartDelay)));

        /// <summary>
        /// A copy writes its source's failure actions — every row, and the reset period — for
        /// as long as the delay it was shown with is left alone. The wizard has one field for
        /// what can be a ladder of restarts with a reboot at the end; changing it asks for the
        /// wizard's own ladder instead, and unticking recovery for none at all.
        /// </summary>
        /// <remarks>
        /// Compared as durations: the delay editor writes its own spelling back as soon as its
        /// unit is picked, and a source's <c>30 secs</c> or <c>30000</c> is still the same wait.
        /// </remarks>
        private bool UsesSourceRecovery =>
            this.clone is { HasRecovery: true }
            && this.restartDelay is { } delay
            && this.cloneRestartDelay is { } shown
            && (ServiceConfigModel.TryParseTime(delay, out var wait) && ServiceConfigModel.TryParseTime(shown, out var shownWait)
                ? wait == shownWait
                : string.Equals(delay.Trim(), shown, StringComparison.Ordinal));

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
        /// Whether two paths name the same file, the way Windows compares them: without regard to
        /// case, and after <c>..</c> and doubled separators. Never for a blank path, and never for
        /// one that is not a path at all — the wrapper's field holds whatever is being typed.
        /// Only the strings are compared; nothing on disk is touched.
        /// </summary>
        internal static bool SamePath(string? path, string? other)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(other))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(path.Trim()), Path.GetFullPath(other.Trim()), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

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

        /// <summary>
        /// One failure action as the recovery hint lists it: a restart by its wait alone, as
        /// the wizard's own ladder is listed, anything else by its name as well. An action
        /// without a delay is taken at once, which is what the wrapper makes of it.
        /// </summary>
        internal static string DescribeFailureAction((string Action, string? Delay) action)
        {
            string delay = string.IsNullOrWhiteSpace(action.Delay) ? "0 sec" : action.Delay.Trim();
            return action.Action switch
            {
                "restart" => delay,
                "none" => action.Action,
                _ => action.Action + " " + delay,
            };
        }

        /// <summary>
        /// The ID and display name a copy of <paramref name="id"/>, shown as <paramref name="name"/>,
        /// is suggested under: the first number from 2 on — <c>api-2</c> and <c>API (2)</c> — for
        /// which nothing known here has either name. Windows refuses a display name that another
        /// service has as its name or display name, as it refuses the ID; see <see cref="ServiceNames"/>.
        /// </summary>
        private CopyNames CopyId(string id, string name)
        {
            // Read once: it is every service on the machine, put together anew on each read.
            var known = this.KnownServices;
            int number = 2;
            while (number < 100 && Taken(Numbered(number)))
            {
                number++;
            }

            return Numbered(number);

            CopyNames Numbered(int n) => new(
                id,
                name,
                id + "-" + n.ToString(CultureInfo.InvariantCulture),
                string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name, n));

            // A desktop task has only its name to clash, with the other tasks.
            bool Taken(CopyNames copy) => this.desktopTask
                ? this.InUse(copy.Id)
                : known.ClashWithId(copy.Id) != null || known.ClashWithDisplayName(copy.DisplayName, copy.Id) != null;
        }

        /// <summary>
        /// Moves a copy's suggested names on to the next free number when a newer reading of the
        /// machine has them taken — by a service that is not WinSW's, which the dashboard does
        /// not list, or by another service's display name. Only a name still holding the
        /// suggestion is changed; one the user has typed stays as typed.
        /// </summary>
        /// <remarks>
        /// Not on the review step, whose findings are about the names it opened with: a name
        /// taken since then is listed there as a problem, and going back renumbers it.
        /// </remarks>
        private void RenumberCopy()
        {
            if (this.copyNames is not { } suggested || this.step == LastStep)
            {
                return;
            }

            bool idHeld = string.Equals(this.serviceId, suggested.Id, StringComparison.Ordinal);
            bool nameHeld = string.Equals(this.displayName, suggested.DisplayName, StringComparison.Ordinal);
            if (!idHeld && !nameHeld)
            {
                return;
            }

            var renumbered = this.CopyId(suggested.BaseId, suggested.BaseName);
            if (renumbered == suggested)
            {
                return;
            }

            this.copyNames = renumbered;
            if (idHeld)
            {
                this.ServiceId = renumbered.Id;
            }

            if (nameHeld)
            {
                this.DisplayName = renumbered.DisplayName;
            }
        }

        /// <summary>
        /// True when <paramref name="id"/> is taken: for a desktop task by a registered task, for
        /// a service by any service on the machine with that name.
        /// </summary>
        private bool InUse(string id) => this.desktopTask
            ? this.TaskSources.Any(t => string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase))
            : this.KnownServices.ClashWithId(id) != null;

        /// <summary>
        /// Every service known to be on the machine: all of them once read, and the ones the
        /// dashboard lists, which are there before the first read comes back.
        /// </summary>
        private ServiceNames KnownServices =>
            this.machineServices.With(this.Sources.Select(s => new InstalledService(s.ServiceName, s.DisplayName)));

        /// <summary>The service already going by the chosen ID; null for a desktop task, which is no service.</summary>
        private InstalledService? IdClash =>
            this.desktopTask || string.IsNullOrWhiteSpace(this.serviceId) ? null : this.KnownServices.ClashWithId(this.serviceId);

        /// <summary>The service already going by the chosen display name; see <see cref="ServiceNames.ClashWithDisplayName"/>.</summary>
        private InstalledService? DisplayNameClash =>
            this.desktopTask ? null : this.KnownServices.ClashWithDisplayName(this.displayName, this.serviceId);

        /// <summary>
        /// A service showing the chosen ID as its display name, which the review step warns of;
        /// see <see cref="ServiceNames.ShownAs"/>. Null when the display name is the ID as well,
        /// where the same service is a display-name clash and a problem already.
        /// </summary>
        private InstalledService? IdShownAs =>
            this.desktopTask
            || string.IsNullOrWhiteSpace(this.serviceId)
            || string.Equals(this.displayName.Trim(), this.serviceId.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : this.KnownServices.ShownAs(this.serviceId);

        private bool CanLeaveCurrentStep() => this.step switch
        {
            1 => !string.IsNullOrWhiteSpace(this.targetPath) && (this.useBundledWrapper || this.wrapperExists),
            2 => !string.IsNullOrWhiteSpace(this.serviceId) && !this.IdInUse && !this.DisplayNameInUse,
            _ => true,
        };

        private void RaiseNameChecks()
        {
            this.Raise(nameof(this.IdInUse));
            this.Raise(nameof(this.IdInUseHint));
            this.Raise(nameof(this.DisplayNameInUse));
            this.Raise(nameof(this.DisplayNameInUseHint));
        }

        /// <summary>
        /// Why the chosen names cannot be installed, for the review step: each clash names the
        /// service in the way and the error Windows would refuse the install with.
        /// </summary>
        private IEnumerable<string> NameClashes()
        {
            string id = this.serviceId.Trim();
            if (this.desktopTask)
            {
                if (this.IdInUse)
                {
                    yield return Localizer.Format("M.Wiz.IdInUse", id);
                }

                yield break;
            }

            if (this.IdClash is { } withId)
            {
                yield return Localizer.Format("M.Wiz.IdTaken", id, withId.Label);
            }

            if (this.DisplayNameClash is { } withName)
            {
                yield return Localizer.Format("M.Wiz.DisplayNameTaken", this.displayName.Trim(), withName.Label);
            }
        }

        /// <summary>
        /// Takes a newer reading of the machine's services, and on the review step swaps the
        /// name clashes it shows for the ones this reading gives — only when they differ.
        /// </summary>
        private void ApplyServiceNames(ServiceNames names)
        {
            this.machineServices = names;
            this.RenumberCopy();
            this.RaiseNameChecks();
            this.RefreshCommands();

            if (this.step != LastStep)
            {
                return;
            }

            string[] clashes = this.NameClashes().ToArray();
            if (clashes.SequenceEqual(this.shownNameClashes))
            {
                return;
            }

            foreach (string clash in this.shownNameClashes)
            {
                this.Problems.Remove(clash);
            }

            foreach (string clash in clashes)
            {
                this.Problems.Add(clash);
            }

            this.shownNameClashes = clashes;
        }

        /// <summary>
        /// Starts checking the wizard against this machine, off the UI thread: every service's
        /// names always, and on the review step what the configuration will meet when it runs
        /// and whether another service shows the ID as its display name.
        /// A check started later makes this one's results stale.
        /// </summary>
        private void CheckMachine(bool environment)
        {
            int generation = ++this.machineCheckGeneration;
            this.MachineCheck = this.CheckMachineAsync(generation, environment ? this.EnvironmentProbe() : null);
        }

        /// <summary>
        /// Reads the machine's services and runs <see cref="ServiceConfigModel.ValidateEnvironment"/>
        /// on a worker — the service control manager, the file system and, for an account, the
        /// domain controller are all slow when they are slow — and shows what they said back on
        /// the thread that asked.
        /// </summary>
        private async Task CheckMachineAsync(int generation, ServiceConfigModel? probe)
        {
            var read = this.readServices;

            // Which wrapper the service will run is settled before the worker starts, the way the
            // install settles it; whether that is the .NET Framework build is a question for its
            // file, so the paths go along.
            WrapperPlan? wrapper = probe != null && this.framework.TooOldForWrapper
                ? new WrapperPlan(
                    this.useBundledWrapper,
                    this.useBundledWrapper ? string.Empty : this.wrapperPath,
                    this.EffectiveWrapperPath,
                    this.brandWrapper,
                    SamePath(this.EffectiveWrapperPath, this.SharedWrapperPath))
                : null;

            var (names, findings, frameworkBuild) = await Task
                .Run(() => Examine(read, probe, wrapper))
                .ConfigureAwait(true);

            if (generation != this.machineCheckGeneration)
            {
                return;
            }

            this.ApplyServiceNames(names);
            if (probe is null)
            {
                return;
            }

            // Read from the names just taken, so it goes with the machine's findings.
            var warnings = new List<string>();
            if (this.IdShownAs is { } shown)
            {
                warnings.Add(Localizer.Format("M.Wiz.IdIsDisplayName", this.serviceId.Trim(), shown.Label));
            }

            if (frameworkBuild == FrameworkWrapper.Installed)
            {
                warnings.Add(Localizer.Format("M.Wiz.NetFxWrapper", this.framework.Version, NetFramework.OfflineInstaller));
            }
            else if (frameworkBuild == FrameworkWrapper.AlreadyInPlace)
            {
                // Downloading the self-contained build again would not help here: the install
                // would keep this one all the same.
                warnings.Add(Localizer.Format("M.Wiz.NetFxWrapperInPlace", wrapper!.Value.Destination, this.framework.Version, NetFramework.OfflineInstaller));
            }

            warnings.AddRange(findings);
            this.EnvironmentWarnings = warnings.ToArray();
        }

        /// <summary>
        /// The configuration as <see cref="ServiceConfigModel.ValidateEnvironment"/> should see
        /// it: at the path it will be written to, so that <c>%BASE%</c> is the new service's
        /// folder; and without a log directory inside that folder, which cannot exist before
        /// the install and which the wrapper creates on its first start in any case. Left as
        /// it is, every new service would be told its log directory is missing.
        /// </summary>
        private ServiceConfigModel EnvironmentProbe()
        {
            var probe = this.BuildModel();
            string configPath = this.ConfigPath;
            if (configPath.Length == 0)
            {
                return probe;
            }

            probe.FilePath = configPath;
            try
            {
                string folder = Path.GetFullPath(Path.GetDirectoryName(configPath)!);
                string logs = Path.GetFullPath(ConfigPaths.ResolveLogDirectory(probe, configPath));
                if (string.Equals(logs, folder, StringComparison.OrdinalIgnoreCase)
                    || logs.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    probe.LogPath = null;
                }
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                // A log path that does not resolve is left for the check to report as it is.
            }

            return probe;
        }

        /// <summary>
        /// The worker's half of <see cref="CheckMachineAsync"/>: the services, what the probe's
        /// check finds, and — only when there is a <paramref name="wrapper"/> to ask about —
        /// whether the service would run the .NET Framework build this machine cannot run.
        /// </summary>
        private static (ServiceNames Names, IReadOnlyList<string> Findings, FrameworkWrapper FrameworkBuild) Examine(
            Func<ServiceNames> read,
            ServiceConfigModel? probe,
            WrapperPlan? wrapper)
        {
            var names = read();
            if (probe is null)
            {
                return (names, Array.Empty<string>(), FrameworkWrapper.None);
            }

            IReadOnlyList<string> findings;
            try
            {
                findings = probe.ValidateEnvironment();
            }
            catch (Exception)
            {
                // A check that cannot finish has nothing to say, which is what the editor, where
                // it comes from, makes of it too; the names still stand.
                findings = Array.Empty<string>();
            }

            return (names, findings, wrapper is { } plan ? FrameworkBuildIn(plan) : FrameworkWrapper.None);
        }

        /// <summary>
        /// Whether the wrapper <paramref name="plan"/> ends up registering is the .NET Framework
        /// build, read the way <see cref="InstallAsync"/> decides what to register: a wrapper
        /// already at the destination is kept — the shared one above all, which other services
        /// may be running from — unless a branded copy is made over it; otherwise the service
        /// runs what is copied or branded from the source. Reads the files, so on a worker.
        /// </summary>
        private static FrameworkWrapper FrameworkBuildIn(WrapperPlan plan)
        {
            bool kept = !plan.Brand
                && plan.Destination.Length > 0
                && (plan.Bundled || plan.DestinationIsShared || !SamePath(plan.Destination, plan.Source))
                && ServiceDiscovery.IsWrapperExecutable(plan.Destination);
            if (kept)
            {
                return IsFrameworkBuild(plan.Destination) ? FrameworkWrapper.AlreadyInPlace : FrameworkWrapper.None;
            }

            return plan.Bundled || IsFrameworkBuild(plan.Source) ? FrameworkWrapper.Installed : FrameworkWrapper.None;

            static bool IsFrameworkBuild(string path) =>
                path.Length > 0 && WrapperKind.ReleaseAssetFor(path) == WrapperDownload.FrameworkAsset;
        }

        /// <summary>
        /// Where the service's wrapper comes from, for <see cref="FrameworkBuildIn"/> to read on a
        /// worker: taken on the UI thread, as strings.
        /// </summary>
        /// <param name="Bundled">The source is the wrapper this application carries, a .NET Framework build.</param>
        /// <param name="Source">The file picked or downloaded; empty for the bundled one.</param>
        /// <param name="Destination">Where the service will run it from; see <see cref="EffectiveWrapperPath"/>.</param>
        /// <param name="Brand">A branded copy is made from the source, over whatever is at the destination.</param>
        /// <param name="DestinationIsShared">The destination is the root's shared wrapper, which is never replaced.</param>
        private readonly record struct WrapperPlan(bool Bundled, string Source, string Destination, bool Brand, bool DestinationIsShared);

        /// <summary>The names a copy is suggested under, and the source's names they are numbered from; see <see cref="CopyId"/>.</summary>
        /// <param name="BaseId">The source's ID, which the copy's is numbered from.</param>
        /// <param name="BaseName">The source's display name, or its ID when it has none.</param>
        /// <param name="Id">The ID suggested, such as <c>api-2</c>.</param>
        /// <param name="DisplayName">The display name suggested, such as <c>API (2)</c>.</param>
        private readonly record struct CopyNames(string BaseId, string BaseName, string Id, string DisplayName);

        /// <summary>What <see cref="FrameworkBuildIn"/> found: the .NET Framework build this machine cannot run, and where it would come from.</summary>
        private enum FrameworkWrapper
        {
            /// <summary>Not that build, or not known to be.</summary>
            None,

            /// <summary>The wrapper being installed is that build; the self-contained download is the way round it.</summary>
            Installed,

            /// <summary>The wrapper already at the destination is, and the install keeps it; downloading changes nothing.</summary>
            AlreadyInPlace,
        }

        /// <summary>
        /// Whether the wizard starts on the bundled wrapper: when this build carries one and the
        /// machine can run it. Otherwise it starts on the self-contained download, which brings
        /// its own runtime; a machine whose framework could not be read keeps the bundled one.
        /// </summary>
        internal static bool PrefersBundledWrapper(bool bundledAvailable, NetFrameworkInfo framework) =>
            bundledAvailable && !framework.TooOldForWrapper;

        /// <summary>
        /// Fetches the wrapper build matching this machine from the latest WinSW release, so a
        /// first-time user never has to go looking for WinSW.exe. It goes into a per-user cache
        /// and is installed from there like the bundled one, never next to the program; see
        /// <see cref="WrapperDownload"/>.
        /// </summary>
        private async Task DownloadWrapperAsync()
        {
            this.IsBusy = true;
            this.StatusMessage = Localizer.Get("M.Wiz.FetchingRelease");
            try
            {
                var latest = await UpdateChecker.LatestWrapperAsync().ConfigureAwait(true);
                if (latest is null
                    || WrapperDownload.AssetFor(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture, this.framework.TooOldForWrapper, latest.Assets) is not { } asset)
                {
                    this.StatusMessage = Localizer.Get("M.Wiz.ReleaseUnavailable");
                    return;
                }

                this.StatusMessage = Localizer.Format("M.Dash.Downloading", asset, latest.Version);
                if (await WrapperDownload.FetchAsync(latest.Assets[asset]).ConfigureAwait(true) is not { } downloaded)
                {
                    this.StatusMessage = Localizer.Get("M.Dash.DownloadFailed");
                    return;
                }

                this.UseDownloadedWrapper(downloaded);
                this.StatusMessage = Localizer.Format("M.Wiz.WrapperDownloaded", latest.Version, downloaded);
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

        /// <summary>
        /// Takes <paramref name="file"/>, just downloaded into the cache, as the wrapper: the
        /// service then gets the root's layout, and the install copies the file into place.
        /// </summary>
        internal void UseDownloadedWrapper(string file)
        {
            // Remembered first: the setters below ask where the service goes, and the answer
            // depends on it.
            this.downloadedWrapper = file;
            this.UseBundledWrapper = false;
            this.WrapperPath = file;

            // The download may have landed on the path already chosen, in which case the setter
            // saw no change and did not re-read; the file exists now either way.
            this.wrapperExists = File.Exists(file);
            this.Raise(nameof(this.InstallDirectory));
            this.Raise(nameof(this.ConfigPath));
            this.Raise(nameof(this.EffectiveWrapperPath));
            this.RefreshCommands();
        }

        public ServiceConfigModel BuildModel()
        {
            // A copy starts from everything its source's file says, so that what the wizard has
            // no field for comes along: the account, stop settings, hooks, dependencies. What
            // it does have a field for is written over it below.
            var model = this.clone?.NewModel() ?? ServiceConfigModel.CreateNew();
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

            // A copy's variables were filled into the wizard's list, where they can be edited;
            // the list is what is written.
            model.EnvironmentVariables.Clear();
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
                // days; the period is left at the wrapper's default of one. A copy keeps the
                // pattern and period its source rolled on.
                model.RollPattern = this.clone?.RollPattern ?? DailyRollPattern;
                model.KeepFiles = NullIfBlank(this.keepDays);
            }

            // Anything else the mode needs — a copy's roll-by-size-time, say — is still as the
            // source's file had it, and the other modes' settings are not written.
            if (this.desktopTask)
            {
                // The wrapper allocates a console so that it can send the child a Ctrl+C on
                // stop. In session 0 nobody sees it; in the session the user is logged on to
                // it would be a black window in front of them for as long as the program runs.
                model.HideWindow = true;

                // Recovery actions belong to the service control manager, which never sees a
                // desktop task. Bringing one of those back up is the trigger's job instead, so
                // a service copied as a task leaves its source's behind.
                model.FailureActions.Clear();
                model.ResetFailureAfter = null;
            }
            else if (!this.restartOnFailure)
            {
                // A new service has no recovery actions for an empty list to leave in place.
                model.FailureActions.Clear();
                model.ResetFailureAfter = null;
            }
            else if (!this.UsesSourceRecovery)
            {
                model.FailureActions.Clear();
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

            // Installing copies the wrapper into place when it is not already there — the bundled
            // one, a downloaded one, a branded copy. If something else already answers to that
            // name, say so before overwriting it.
            if (this.EffectiveWrapperPath is { Length: > 0 } destination
                && (this.useBundledWrapper || !SamePath(destination, this.wrapperPath))
                && File.Exists(destination)
                && !ServiceDiscovery.IsWrapperExecutable(destination))
            {
                this.Problems.Add(Localizer.Format("M.Wiz.WouldOverwrite", destination));
            }

            // Against every service on the machine as last read, and the dashboard's before that;
            // a newer reading swaps these, see ApplyServiceNames.
            this.shownNameClashes = this.NameClashes().ToArray();
            foreach (string clash in this.shownNameClashes)
            {
                this.Problems.Add(clash);
            }

            this.Warnings.Clear();
            if (this.WorkingDirectoryWarning(model) is { } warning)
            {
                this.Warnings.Add(warning);
            }

            foreach (string note in this.CloneWarnings(model))
            {
                this.Warnings.Add(note);
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

        /// <summary>
        /// What a copy probably still has of its source that has to change before both can
        /// run: the port it listens on, a password that may have changed since, log files the
        /// two would fight over, and the source's own files it now runs. Empty for a service
        /// not copied from another.
        /// </summary>
        private IEnumerable<string> CloneWarnings(ServiceConfigModel model)
        {
            if (this.clone is not { } clone)
            {
                yield break;
            }

            // Only the ports the source listens on too: once one is changed, it stops being a
            // problem, and a second port the copy was given is none of the source's business.
            var ports = ServiceClone.FindPorts(string.Join(" ", model.Arguments, model.StartArguments), model.EnvironmentVariables)
                .Where(clone.Ports.Contains)
                .ToList();
            if (ports.Count > 0)
            {
                yield return Localizer.Format("M.Wiz.ClonePort", string.Join(", ", ports), clone.SourceName);
            }

            // A task runs as whoever registers it; the account is a service's alone.
            if (!this.desktopTask && ServiceClone.CarriesPassword(model))
            {
                yield return Localizer.Format("M.Wiz.ClonePassword", model.ServiceAccountUser!.Trim(), clone.SourceName);
            }

            string configPath = this.ConfigPath;
            if (clone.SharesLogFilesWith(model, configPath))
            {
                yield return Localizer.Format("M.Wiz.CloneLogFiles", clone.SourceName, ConfigPaths.ResolveLogDirectory(model, configPath));
            }

            if (clone.PointingIntoSource(model) is { Count: > 0 } settings)
            {
                yield return Localizer.Format("M.Wiz.CloneRebased", clone.SourceName, clone.SourceDirectory, string.Join(", ", settings));
            }
        }

        private async Task InstallAsync()
        {
            // Busy from the start: the names are read again below, off the UI thread, and Back
            // must not be clickable while an install that has not been refused yet waits on it.
            this.IsBusy = true;
            try
            {
                // The names were read when the review step opened. A service installed since
                // then, by anyone, would still be refused — after the UAC prompt, with the
                // files written.
                if (!this.desktopTask)
                {
                    var read = this.readServices;
                    this.ApplyServiceNames(await Task.Run(read).ConfigureAwait(true));
                }

                this.RefreshPreview();

                var model = this.BuildModel();
                if (model.Validate().Count > 0 || this.IdInUse || this.DisplayNameInUse)
                {
                    this.StatusMessage = Localizer.Get("M.Wiz.FixProblems");
                    return;
                }

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

        /// <summary>
        /// Fills the wizard in from an installed service, keeping its whole configuration for
        /// <see cref="BuildModel"/> to start from; see <see cref="ServiceClone"/>.
        /// </summary>
        /// <remarks>
        /// The wrapper and the folder are left as they are for any new service: the copy gets
        /// a folder of its own under the install root and the wrapper shared from there. The
        /// source's wrapper is no guide to either — the shared one would put the copy's file
        /// in <c>bin</c>, and a branded one in the source's own folder.
        /// </remarks>
        private void PrefillFrom(ServiceEntry entry)
        {
            if (entry.ConfigPath is null)
            {
                return;
            }

            ServiceClone copied;
            try
            {
                copied = ServiceClone.Load(entry.ServiceName, entry.ConfigPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Nothing is changed: what the wizard held, from an earlier copy or typed in,
                // is still whole.
                this.StatusMessage = Localizer.Format("M.Wiz.CloneFailed", e.Message);
                return;
            }

            this.clone = copied;
            var model = copied.NewModel();

            // The source's variables go in first, so that a Python program is offered only
            // the ones it does not have yet; an earlier copy's are replaced, not added to.
            this.EnvironmentVariables.Clear();
            this.offeredVariables.Clear();
            this.pythonEnvironmentOffered = false;
            foreach (var variable in model.EnvironmentVariables)
            {
                this.EnvironmentVariables.Add(new EnvironmentVariable { Name = variable.Name, Value = variable.Value });
            }

            this.TargetPath = model.Executable;
            this.Arguments = model.Arguments ?? string.Empty;

            // The program may be the one already there, which the setter does not look at again.
            this.OfferPythonEnvironment();

            // Set after the program, whose own suggestion would otherwise stand; the source's
            // is never empty — see ServiceClone.
            this.WorkingDirectory = model.WorkingDirectory ?? string.Empty;

            var names = this.CopyId(model.Id, string.IsNullOrWhiteSpace(model.DisplayName) ? model.Id : model.DisplayName);
            this.copyNames = names;
            this.ServiceId = names.Id;
            this.DisplayName = names.DisplayName;
            this.Description = model.Description ?? string.Empty;
            this.StartMode = model.StartMode;
            this.DelayedAutoStart = model.DelayedAutoStart;

            // A mode the wizard has no fields for is offered as well, and written with the
            // settings the source had for it. One the wrapper would not know is not copied.
            string mode = model.LogMode;
            if (Array.IndexOf(ServiceConfigModel.LogModes, mode) < 0)
            {
                mode = "roll-by-size";
            }

            this.LogModes = OfferedLogModes.Contains(mode) ? OfferedLogModes : OfferedLogModes.Append(mode).ToArray();
            this.LogMode = mode;
            this.LogPath = model.LogPath ?? string.Empty;
            this.SizeThresholdKb = model.SizeThreshold ?? "10240";

            // A time-rolled source's count is a count of files on its own pattern, which the
            // copy keeps. Without one it kept every file, and so does the copy.
            bool byTime = mode == "roll-by-time";
            this.KeepFiles = (byTime ? null : model.KeepFiles) ?? "8";
            this.KeepDays = byTime ? model.KeepFiles ?? string.Empty : "30";

            // The source's actions are shown by their first restart's delay, and written as they
            // are for as long as that is left alone; see UsesSourceRecovery. A source whose only
            // row says none — the editor writes that when the last row is removed — has none
            // to copy, and must not come back as a restart.
            this.RestartOnFailure = copied.HasRecovery;
            this.cloneRestartDelay = copied.ShownRecovery is { } shown
                ? DescribeDelay(shown.Delay)
                : "10 sec";
            this.RestartDelay = this.cloneRestartDelay;

            this.Raise(nameof(this.RecoveryHint));
            this.Raise(nameof(this.KeepsSourceRollPattern));
            this.Raise(nameof(this.RollPatternHint));
            this.StatusMessage = Localizer.Format("M.Wiz.Cloned", entry.ServiceName);

            // The names were checked against what the dashboard lists, which is WinSW's services
            // alone, and the machine is not read until step 2 opens. Read it now, off the UI
            // thread, so that the suggestion has moved past a taken name before anyone sees it.
            this.CheckMachine(environment: this.step == LastStep);

            static string DescribeDelay(string? delay) => string.IsNullOrWhiteSpace(delay) ? "0 sec" : delay.Trim();
        }

        private void Reset()
        {
            this.CloneSource = null;
            this.clone = null;
            this.copyNames = null;
            this.cloneRestartDelay = null;
            this.downloadedWrapper = null;
            this.UseBundledWrapper = PrefersBundledWrapper(BundledWrapper.IsAvailable, this.framework);
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

            // The mode first, so that it is in the list the picker is handed next.
            this.LogMode = "roll-by-size";
            this.LogModes = OfferedLogModes;
            this.LogPath = string.Empty;
            this.SizeThresholdKb = "10240";
            this.KeepFiles = "8";
            this.KeepDays = "30";
            this.RestartOnFailure = true;
            this.RestartDelay = "10 sec";
            this.Raise(nameof(this.RecoveryHint));
            this.Raise(nameof(this.KeepsSourceRollPattern));
            this.Raise(nameof(this.RollPatternHint));
            this.LogonDelay = "30 sec";
            this.KeepAliveInterval = "1 min";
            this.RunElevated = false;
            this.StartAfterInstall = true;
            this.StatusMessage = string.Empty;
            this.ConfigPreview = string.Empty;
            this.Problems.Clear();
            this.Warnings.Clear();

            // A check still out is about the service being dropped; its findings go with it.
            this.machineCheckGeneration++;
            this.shownNameClashes = Array.Empty<string>();
            this.EnvironmentWarnings = Array.Empty<string>();

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
