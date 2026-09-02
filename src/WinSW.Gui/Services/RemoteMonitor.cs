using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>The state of one service on another machine.</summary>
    public sealed class RemoteServiceStatus
    {
        public RemoteServiceStatus(string serviceName, string displayName, ServiceControllerStatus? status, string? error)
        {
            this.ServiceName = serviceName;
            this.DisplayName = displayName;
            this.Status = status;
            this.Error = error;
        }

        public string ServiceName { get; }

        public string DisplayName { get; }

        public ServiceControllerStatus? Status { get; }

        public string? Error { get; }

        public bool IsRunning => this.Status == ServiceControllerStatus.Running;
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
        public static IReadOnlyList<RemoteServiceStatus> List(string machine, string? filter)
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

                    if (!string.IsNullOrWhiteSpace(filter)
                        && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        && !display.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
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
