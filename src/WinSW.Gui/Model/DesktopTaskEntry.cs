using System;
using System.IO;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;
using WinSW.Gui.Services;

namespace WinSW.Gui.Model
{
    /// <summary>
    /// One program hosted in the logged-on session by a scheduled task, rather than in
    /// session 0 by the service control manager.
    /// </summary>
    /// <remarks>
    /// The two are deliberately separate types. A service and a desktop task share the
    /// configuration file and the wrapper that reads it, and nothing else: the states they
    /// can be in, the ways they start, and the things that can go wrong with them all come
    /// from different subsystems.
    /// </remarks>
    public sealed class DesktopTaskEntry : ObservableObject
    {
        private DesktopTaskState state;
        private bool enabled;
        private DateTime? lastRun;
        private int lastResult;
        private string displayName;
        private string? problem;

        public DesktopTaskEntry(DesktopTaskInfo info)
        {
            this.Name = info.Name;
            this.ConfigPath = info.ConfigPath;
            this.WrapperPath = info.WrapperPath;
            this.UserId = info.UserId;
            this.RunElevated = info.RunElevated;
            this.Description = info.Description;
            this.displayName = info.Name;
            this.Apply(info);
            this.ReadConfiguration();
        }

        /// <summary>The registered task name, which is the configuration's service ID.</summary>
        public string Name { get; }

        public string ConfigPath { get; }

        public string WrapperPath { get; }

        /// <summary>The account whose logon starts the task and whose session it runs in.</summary>
        public string UserId { get; }

        public bool RunElevated { get; }

        public string Description { get; }

        /// <summary>The configuration's display name, falling back to the task name.</summary>
        public string DisplayName
        {
            get => this.displayName;
            private set => this.Set(ref this.displayName, value);
        }

        public DesktopTaskState State
        {
            get => this.state;
            private set
            {
                if (this.Set(ref this.state, value))
                {
                    this.RaiseDerived();
                }
            }
        }

        public bool Enabled
        {
            get => this.enabled;
            private set
            {
                if (this.Set(ref this.enabled, value))
                {
                    this.RaiseDerived();
                }
            }
        }

        public DateTime? LastRun
        {
            get => this.lastRun;
            private set
            {
                if (this.Set(ref this.lastRun, value))
                {
                    this.Raise(nameof(this.LastRunText));
                }
            }
        }

        /// <summary>The exit code of the last run; 267009 while the task is still running.</summary>
        public int LastResult
        {
            get => this.lastResult;
            private set
            {
                if (this.Set(ref this.lastResult, value))
                {
                    this.Raise(nameof(this.LastResultText));
                }
            }
        }

        /// <summary>Set when the task points at a configuration or wrapper that is not there.</summary>
        public string? Problem
        {
            get => this.problem;
            private set
            {
                if (this.Set(ref this.problem, value))
                {
                    this.Raise(nameof(this.HasProblem));
                    this.RaiseDerived();
                }
            }
        }

        public bool HasProblem => !string.IsNullOrEmpty(this.problem);

        public ServiceHealth Health =>
            this.problem != null ? ServiceHealth.Broken :
            this.state == DesktopTaskState.Running ? ServiceHealth.Running :
            this.state == DesktopTaskState.Unknown ? ServiceHealth.Unknown :
            !this.enabled ? ServiceHealth.Unknown :
            ServiceHealth.Stopped;

        public string StatusText => Localizer.Get(
            this.problem != null ? "M.Task.State.Broken" :
            this.state == DesktopTaskState.Running ? "M.Status.Running" :
            !this.enabled || this.state == DesktopTaskState.Disabled ? "M.Task.State.Disabled" :
            this.state == DesktopTaskState.Queued ? "M.Status.Starting" :
            this.state == DesktopTaskState.Ready ? "M.Status.Stopped" :
            "M.Status.Unknown");

        public string LastRunText => this.lastRun is { } run
            ? run.ToString("yyyy-MM-dd HH:mm:ss")
            : "—";

        /// <summary>
        /// 0 is a clean exit; 267009 is the scheduler's "still running"; 267011 means it has
        /// never run. Anything else is the exit code of the program the wrapper hosted.
        /// </summary>
        public string LastResultText => this.lastResult switch
        {
            0 => "0",
            267009 => "—",
            267011 => "—",
            _ => this.lastResult.ToString(),
        };

        public bool CanStart => this.problem is null && this.state is DesktopTaskState.Ready or DesktopTaskState.Queued;

        public bool CanStop => this.state == DesktopTaskState.Running;

        /// <summary>The directory logs default to when the configuration does not override it.</summary>
        public string? DefaultLogDirectory =>
            string.IsNullOrEmpty(this.ConfigPath) ? null : Path.GetDirectoryName(this.ConfigPath);

        /// <summary>Folds a fresh reading from the task scheduler into this entry.</summary>
        public void Apply(DesktopTaskInfo info)
        {
            this.State = info.State;
            this.Enabled = info.Enabled;
            this.LastRun = info.LastRun;
            this.LastResult = info.LastResult;

            this.Problem =
                string.IsNullOrEmpty(info.ConfigPath) ? Localizer.Format("M.Task.NoConfig", info.Name) :
                !File.Exists(info.ConfigPath) ? Localizer.Format("M.Discovery.ConfigMissing", info.ConfigPath) :
                !File.Exists(info.WrapperPath) ? Localizer.Format("M.Cli.WrapperMissing", info.WrapperPath) :
                null;
        }

        /// <summary>Re-evaluates the localized text after a language change.</summary>
        public void RefreshLocalized()
        {
            this.Raise(nameof(this.StatusText));
            this.Raise(nameof(this.LastRunText));
        }

        public override string ToString() => this.Name;

        private void RaiseDerived()
        {
            this.Raise(nameof(this.Health));
            this.Raise(nameof(this.StatusText));
            this.Raise(nameof(this.CanStart));
            this.Raise(nameof(this.CanStop));
        }

        private void ReadConfiguration()
        {
            if (string.IsNullOrEmpty(this.ConfigPath) || !File.Exists(this.ConfigPath))
            {
                return;
            }

            try
            {
                var model = ServiceConfigModel.Load(this.ConfigPath);
                if (!string.IsNullOrWhiteSpace(model.DisplayName))
                {
                    this.DisplayName = model.DisplayName!;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
            {
                // The configuration is reported as a problem by Apply; a missing friendly
                // name only costs the task its own ID as a caption.
            }
        }
    }
}
