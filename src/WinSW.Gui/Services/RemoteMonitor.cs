using System;
using System.Collections.Generic;
using System.ServiceProcess;
using WinSW.Gui.Localization;
using WinSW.Gui.Mvvm;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The state of one service on another machine. Observable so a refresh can update the
    /// row already on screen instead of replacing it, which would reset the list's scroll.
    /// </summary>
    public sealed class RemoteServiceStatus : ObservableObject
    {
        private string displayName;
        private ServiceControllerStatus? status;
        private string? error;

        public RemoteServiceStatus(string serviceName, string displayName, ServiceControllerStatus? status, string? error)
        {
            this.ServiceName = serviceName;
            this.displayName = displayName;
            this.status = status;
            this.error = error;
        }

        public string ServiceName { get; }

        public string DisplayName
        {
            get => this.displayName;
            private set => this.Set(ref this.displayName, value);
        }

        public ServiceControllerStatus? Status
        {
            get => this.status;
            private set
            {
                if (this.Set(ref this.status, value))
                {
                    this.Raise(nameof(this.IsRunning));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.CanStop));
                }
            }
        }

        public string? Error
        {
            get => this.error;
            private set => this.Set(ref this.error, value);
        }

        public bool IsRunning => this.Status == ServiceControllerStatus.Running;

        public bool CanStart => this.Status == ServiceControllerStatus.Stopped;

        public bool CanStop => this.Status is ServiceControllerStatus.Running or ServiceControllerStatus.Paused;

        /// <summary>Takes on a newer reading of the same service.</summary>
        public void CopyFrom(RemoteServiceStatus newer)
        {
            this.DisplayName = newer.DisplayName;
            this.Status = newer.Status;
            this.Error = newer.Error;
        }
    }

    /// <summary>What the Remote page can do to a service on another machine.</summary>
    public enum RemoteAction
    {
        Start,
        Stop,
        Restart,
    }

    /// <summary>
    /// Services on other machines, through the service control manager's RPC interface (the
    /// same channel the Services console uses when you connect to another computer), with the
    /// caller's own rights there.
    /// </summary>
    /// <remarks>
    /// Starting and stopping are the service control manager's own operations, so they need
    /// nothing on the other machine but the right to ask: no wrapper, no remote shell.
    /// Installing, uninstalling and refreshing do need the wrapper there, and stay out of reach.
    /// </remarks>
    public static class RemoteMonitor
    {
        /// <summary>
        /// How long a remote start or stop is waited for. The service's own stop timeout lives
        /// in a configuration on the other machine, which is not read from here.
        /// </summary>
        public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Starts, stops or restarts <paramref name="serviceName"/> and waits for it to settle.</summary>
        /// <exception cref="InvalidOperationException">Refused, unreachable or too slow; the message says which.</exception>
        public static void Control(string machine, string serviceName, RemoteAction action)
        {
            using var service = new ServiceController(serviceName, machine);
            try
            {
                if (action is RemoteAction.Stop or RemoteAction.Restart && service.Status != ServiceControllerStatus.Stopped)
                {
                    // Not Stop(), which stops every dependent service first without a word.
                    // Asked this way the service control manager refuses a service others depend
                    // on, and the page says so, as the console's own Stop does.
                    service.Stop(stopDependentServices: false);
                    service.WaitForStatus(ServiceControllerStatus.Stopped, ControlTimeout);
                }

                if (action is RemoteAction.Start or RemoteAction.Restart)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, ControlTimeout);
                }
            }
            catch (InvalidOperationException e) when (e.InnerException is System.ComponentModel.Win32Exception inner)
            {
                throw new InvalidOperationException(Describe(inner, machine), e);
            }
            catch (System.ServiceProcess.TimeoutException e)
            {
                throw new InvalidOperationException(Localizer.Format("M.Remote.TimedOut", (int)ControlTimeout.TotalSeconds), e);
            }
        }

        private static string Describe(System.ComponentModel.Win32Exception error, string machine) => error.NativeErrorCode switch
        {
            5 => Localizer.Format("M.Remote.Denied", machine),
            CommandResult.DependentServicesRunning => Localizer.Get("M.Cli.HasDependents"),
            1056 => Localizer.Get("M.Cli.AlreadyRunning"),
            1062 => Localizer.Get("M.Cli.NotRunning"),
            _ => error.Message,
        };

        /// <exception cref="InvalidOperationException">The machine cannot be reached or refuses the query.</exception>
        public static IReadOnlyList<RemoteServiceStatus> List(string machine)
        {
            var results = new List<RemoteServiceStatus>();
            ServiceController[] services;

            try
            {
                services = ServiceController.GetServices(machine);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException)
            {
                throw new InvalidOperationException(e.Message, e);
            }

            foreach (var service in services)
            {
                using (service)
                {
                    string name = service.ServiceName;
                    string display;
                    try
                    {
                        display = service.DisplayName;
                    }
                    catch (InvalidOperationException)
                    {
                        display = name;
                    }

                    try
                    {
                        results.Add(new RemoteServiceStatus(name, display, service.Status, null));
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        results.Add(new RemoteServiceStatus(name, display, null, e.Message));
                    }
                }
            }

            results.Sort(static (x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));
            return results;
        }
    }
}
