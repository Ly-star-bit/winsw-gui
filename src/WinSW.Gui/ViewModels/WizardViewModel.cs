using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using WinSW.Gui.Model;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

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

        public WizardViewModel()
        {
            this.NextCommand = new RelayCommand(() => this.Step++, () => this.step < LastStep && this.CanLeaveCurrentStep());
            this.BackCommand = new RelayCommand(() => this.Step--, () => this.step > 1);
            this.InstallCommand = new AsyncRelayCommand(this.InstallAsync, () => this.step == LastStep && !this.isBusy);
            this.ResetCommand = new RelayCommand(this.Reset);

            this.BrowseWrapperCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile("Select the WinSW executable", "WinSW executable|*.exe") is { } path)
                {
                    this.WrapperPath = path;
                }
            });

            this.BrowseTargetCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile("Select the program to run as a service", "Programs|*.exe;*.bat;*.cmd;*.jar|All files|*.*") is { } path)
                {
                    this.TargetPath = path;
                }
            });

            this.BrowseWorkingDirectoryCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder("Select the working directory", this.workingDirectory) is { } path)
                {
                    this.WorkingDirectory = path;
                }
            });

            this.BrowseLogPathCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFolder("Select the log directory", this.logPath) is { } path)
                {
                    this.LogPath = path;
                }
            });
        }

        /// <summary>Raised after a successful installation so the shell can show the new service.</summary>
        public event Action? Completed;

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

        public string StepTitle => this.step switch
        {
            1 => "Choose the wrapper and the program",
            2 => "Describe the service",
            3 => "Logging and recovery",
            _ => "Review and install",
        };

        // Step 1 ---------------------------------------------------------------

        /// <summary>The WinSW executable that will host the service.</summary>
        public string WrapperPath
        {
            get => this.wrapperPath;
            set
            {
                if (this.Set(ref this.wrapperPath, value))
                {
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
                    this.RefreshCommands();
                }
            }
        }

        public string Arguments
        {
            get => this.arguments;
            set => this.Set(ref this.arguments, value);
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
                    this.Raise(nameof(this.ConfigPath));
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
                if (string.IsNullOrWhiteSpace(this.wrapperPath) || string.IsNullOrWhiteSpace(this.serviceId))
                {
                    return string.Empty;
                }

                return Path.Combine(Path.GetDirectoryName(this.wrapperPath) ?? string.Empty, this.serviceId + ".xml");
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

            if (string.IsNullOrWhiteSpace(this.workingDirectory))
            {
                this.WorkingDirectory = Path.GetDirectoryName(this.targetPath) ?? string.Empty;
            }
        }

        private bool CanLeaveCurrentStep() => this.step switch
        {
            1 => File.Exists(this.wrapperPath) && !string.IsNullOrWhiteSpace(this.targetPath),
            2 => !string.IsNullOrWhiteSpace(this.serviceId),
            _ => true,
        };

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

            if (this.restartOnFailure)
            {
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
                this.Problems.Add($"'{this.ConfigPath}' already exists and will be overwritten.");
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
                this.StatusMessage = "Fix the reported problems before installing.";
                return;
            }

            this.IsBusy = true;
            try
            {
                string configPath = this.ConfigPath;
                this.StatusMessage = $"Writing {configPath}…";

                try
                {
                    model.Save(configPath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    this.StatusMessage = $"Could not write the configuration: {e.Message}";
                    return;
                }

                this.StatusMessage = $"Installing '{model.Id}'…";
                var install = await WinSwCli.InstallAsync(this.wrapperPath, configPath).ConfigureAwait(true);
                if (!install.Succeeded)
                {
                    this.StatusMessage = install.Cancelled
                        ? "Elevation was declined. The configuration was written, but the service was not installed."
                        : install.Error ?? "Installation failed.";
                    return;
                }

                if (this.startAfterInstall)
                {
                    this.StatusMessage = $"Starting '{model.Id}'…";
                    var start = await WinSwCli.StartAsync(this.wrapperPath, configPath).ConfigureAwait(true);
                    this.StatusMessage = start.Succeeded
                        ? $"'{model.Id}' was installed and started."
                        : $"'{model.Id}' was installed, but starting it failed: {start.Error ?? "elevation was declined"}.";
                }
                else
                {
                    this.StatusMessage = $"'{model.Id}' was installed.";
                }

                this.Completed?.Invoke();
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        private void Reset()
        {
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
        }
    }
}
