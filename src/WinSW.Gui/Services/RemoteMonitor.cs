using System;
using System.Collections.Generic;
using System.ServiceProcess;
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
                }
            }
        }

        public string? Error
        {
            get => this.error;
            private set => this.Set(ref this.error, value);
        }

        public bool IsRunning => this.Status == ServiceControllerStatus.Running;

        /// <summary>Takes on a newer reading of the same service.</summary>
        public void CopyFrom(RemoteServiceStatus newer)
        {
            this.DisplayName = newer.DisplayName;
            this.Status = newer.Status;
            this.Error = newer.Error;
        }
    }

    /// <summary>
    /// Read-only view of services on other machines, through the service control manager's
    /// RPC interface (the same channel the Services console uses when you connect to another
    /// computer). Requires the caller to be allowed to query the remote SCM; nothing is ever
    /// changed remotely from here.
    /// </summary>
    public static class RemoteMonitor
    {
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
