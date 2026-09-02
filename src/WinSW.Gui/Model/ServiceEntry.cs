using System;
using System.IO;
using System.ServiceProcess;
using WinSW.Gui.Mvvm;

namespace WinSW.Gui.Model
{
    public enum ServiceHealth
    {
        Unknown,
        Running,
        Stopped,
        Pending,
        Broken,
    }

    /// <summary>
    /// One installed Windows service that is hosted by a WinSW wrapper executable.
    /// </summary>
    public sealed class ServiceEntry : ObservableObject
    {
        private ServiceControllerStatus? status;
        private int processId;
        private string? problem;

        public ServiceEntry(string serviceName, string displayName, string wrapperPath, string? configPath)
        {
            this.ServiceName = serviceName;
            this.DisplayName = displayName;
            this.WrapperPath = wrapperPath;
            this.ConfigPath = configPath;
        }

        public string ServiceName { get; }

        public string DisplayName { get; }

        /// <summary>The WinSW executable registered as the service image.</summary>
        public string WrapperPath { get; }

        /// <summary>
        /// The configuration the wrapper was installed with, resolved either from the
        /// service's command line or from the executable's own name.
        /// </summary>
        public string? ConfigPath { get; }

        public string Description { get; init; } = string.Empty;

        public string StartMode { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;

        public ServiceControllerStatus? Status
        {
            get => this.status;
            set
            {
                if (this.Set(ref this.status, value))
                {
                    this.Raise(nameof(this.Health));
                    this.Raise(nameof(this.StatusText));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.CanStop));
                }
            }
        }

        public int ProcessId
        {
            get => this.processId;
            set
            {
                if (this.Set(ref this.processId, value))
                {
                    this.Raise(nameof(this.ProcessIdText));
                }
            }
        }

        public string ProcessIdText => this.processId > 0 ? $"PID {this.processId}" : "—";

        /// <summary>Set when the service is installed but its configuration is unusable.</summary>
        public string? Problem
        {
            get => this.problem;
            set
            {
                if (this.Set(ref this.problem, value))
                {
                    this.Raise(nameof(this.Health));
                    this.Raise(nameof(this.HasProblem));
                }
            }
        }

        public bool HasProblem => !string.IsNullOrEmpty(this.problem);

        public ServiceHealth Health => this.problem != null ? ServiceHealth.Broken : this.status switch
        {
            ServiceControllerStatus.Running => ServiceHealth.Running,
            ServiceControllerStatus.Stopped => ServiceHealth.Stopped,
            null => ServiceHealth.Unknown,
            _ => ServiceHealth.Pending,
        };

        public string StatusText => this.status switch
        {
            ServiceControllerStatus.Running => "Running",
            ServiceControllerStatus.Stopped => "Stopped",
            ServiceControllerStatus.StartPending => "Starting",
            ServiceControllerStatus.StopPending => "Stopping",
            ServiceControllerStatus.PausePending => "Pausing",
            ServiceControllerStatus.ContinuePending => "Resuming",
            ServiceControllerStatus.Paused => "Paused",
            _ => "Unknown",
        };

        public bool CanStart => this.status == ServiceControllerStatus.Stopped;

        public bool CanStop => this.status == ServiceControllerStatus.Running
            || this.status == ServiceControllerStatus.Paused;

        /// <summary>The directory logs default to when the configuration does not override it.</summary>
        public string? DefaultLogDirectory =>
            this.ConfigPath is null ? null : Path.GetDirectoryName(this.ConfigPath);

        public override string ToString() => this.ServiceName;
    }
}
