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
        private string manufacturer = string.Empty;
        private ServiceEntry? cloneSource;

        public WizardViewModel()
        {
            this.NextCommand = new RelayCommand(() => this.Step++, () => this.step < LastStep && this.CanLeaveCurrentStep());
            this.BackCommand = new RelayCommand(() => this.Step--, () => this.step > 1);
            this.InstallCommand = new AsyncRelayCommand(this.InstallAsync, () => this.step == LastStep && !this.isBusy);
            this.ResetCommand = new RelayCommand(this.Reset);

            this.BrowseWrapperCommand = new RelayCommand(() =>
            {
                if (Dialogs.PickFile(Localizer.Get("M.Dlg.SelectWrapper"), Localizer.Get("M.Filter.Wrapper")) is { } path)
                {
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
                if (this.step == LastStep)
                {
                    this.RefreshPreview();
                }
            };
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
        public string EffectiveWrapperPath =>
            this.brandWrapper && !string.IsNullOrWhiteSpace(this.serviceId) && !string.IsNullOrWhiteSpace(this.wrapperPath)
                ? Path.Combine(Path.GetDirectoryName(this.wrapperPath) ?? string.Empty, this.serviceId + ".exe")
                : this.wrapperPath;

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
                    this.Raise(nameof(this.EffectiveWrapperPath));
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
                this.Problems.Add(Localizer.Format("M.Wiz.Exists", this.ConfigPath));
            }

            if (File.Exists(this.wrapperPath) && !ServiceDiscovery.IsWrapperExecutable(this.wrapperPath))
            {
                this.Problems.Add(Localizer.Format("M.Wiz.NotWrapper", this.wrapperPath));
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
                string wrapper = this.wrapperPath;

                if (this.brandWrapper)
                {
                    string branded = this.EffectiveWrapperPath;
                    this.StatusMessage = Localizer.Format("M.Wiz.Branding", branded);

                    var customized = await WinSwCli.CustomizeAsync(this.wrapperPath, branded, string.IsNullOrWhiteSpace(this.manufacturer) ? model.DisplayName ?? model.Id : this.manufacturer.Trim()).ConfigureAwait(true);
                    if (!customized.Succeeded)
                    {
                        this.StatusMessage = Localizer.Format("M.Wiz.BrandFailed", customized.Error ?? string.Empty);
                        return;
                    }

                    wrapper = branded;
                }

                this.StatusMessage = Localizer.Format("M.Wiz.Writing", configPath);

                if (!await this.WriteConfigurationAsync(model, configPath).ConfigureAwait(true))
                {
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
                this.Completed?.Invoke();
            }
            finally
            {
                this.IsBusy = false;
            }
        }

        /// <summary>
        /// Writes next to the wrapper, which is often under Program Files; when that is not
        /// writable for a standard user the file is staged and copied with elevation.
        /// </summary>
        private async Task<bool> WriteConfigurationAsync(ServiceConfigModel model, string configPath)
        {
            try
            {
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
