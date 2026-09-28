using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>One service on the machine, by the two names the service control manager knows it by.</summary>
    public readonly record struct InstalledService(string Name, string DisplayName)
    {
        /// <summary>How a message names it: <c>Print Spooler (Spooler)</c>, or the name alone when that is all there is.</summary>
        public string Label => this.DisplayName.Length == 0 || string.Equals(this.DisplayName, this.Name, StringComparison.OrdinalIgnoreCase)
            ? this.Name
            : this.DisplayName + " (" + this.Name + ")";
    }

    /// <summary>
    /// Every name the services on a machine already go by — WinSW's and everyone else's,
    /// drivers included — which a new service may not take.
    /// </summary>
    /// <remarks>
    /// The service control manager compares names and display names without regard to case.
    /// As MS-SCMR documents <c>RCreateServiceW</c>, a new service named like another is refused
    /// with 1073 (ERROR_SERVICE_EXISTS), and one whose display name is another's display name
    /// or name with 1078 (ERROR_DUPLICATE_SERVICE_NAME). Either comes back from
    /// <c>winsw install</c>, after the UAC prompt and with the configuration already written, so
    /// the wizard asks first — and it can see only the services WinSW hosts unless it reads
    /// them all. A name that another service shows as its display name is not on that list;
    /// the wizard only warns about it, see <see cref="ShownAs"/>.
    /// </remarks>
    public sealed class ServiceNames
    {
        /// <summary>Nothing read yet, or nothing that could be.</summary>
        public static readonly ServiceNames None = new(Array.Empty<InstalledService>());

        private readonly InstalledService[] services;

        public ServiceNames(IEnumerable<InstalledService> services)
        {
            this.services = services.ToArray();
        }

        public int Count => this.services.Length;

        /// <summary>
        /// Reads every service and driver on this machine. One call into the service control
        /// manager each, which belongs off the UI thread like any other.
        /// </summary>
        /// <remarks>
        /// A list that cannot be read is an empty one: the wizard then refuses nothing on its
        /// account, and the install reports whatever Windows says, as it did before.
        /// </remarks>
        public static ServiceNames Read()
        {
            var found = new List<InstalledService>();
            Add(ServiceController.GetServices);
            Add(ServiceController.GetDevices);
            return new ServiceNames(found);

            void Add(Func<ServiceController[]> enumerate)
            {
                ServiceController[] controllers;
                try
                {
                    controllers = enumerate();
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException or TypeInitializationException)
                {
                    return;
                }

                foreach (var controller in controllers)
                {
                    using (controller)
                    {
                        found.Add(new InstalledService(controller.ServiceName, DisplayNameOf(controller)));
                    }
                }
            }
        }

        /// <summary>These and <paramref name="more"/>, such as the services a list already on screen holds.</summary>
        public ServiceNames With(IEnumerable<InstalledService> more) => new(this.services.Concat(more));

        /// <summary>
        /// The service already named <paramref name="id"/>, next to which Windows refuses a new
        /// one with 1073; null when there is none.
        /// </summary>
        public InstalledService? ClashWithId(string id)
        {
            string wanted = id.Trim();
            if (wanted.Length == 0)
            {
                return null;
            }

            foreach (var service in this.services)
            {
                if (string.Equals(service.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return service;
                }
            }

            return null;
        }

        /// <summary>
        /// A service, other than one named <paramref name="name"/>, that shows it as its display
        /// name; null when there is none.
        /// </summary>
        /// <remarks>
        /// For a display name that is a clash Windows refuses with 1078. For a new service's
        /// name it is not one Windows documents, and the wizard only warns: holding up an
        /// install Windows would have taken is worse than being told of a doubt.
        /// </remarks>
        public InstalledService? ShownAs(string name)
        {
            string wanted = name.Trim();
            if (wanted.Length == 0)
            {
                return null;
            }

            foreach (var service in this.services)
            {
                if (string.Equals(service.DisplayName, wanted, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(service.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return service;
                }
            }

            return null;
        }

        /// <summary>
        /// The service that stands in the way of a new one showing <paramref name="displayName"/>:
        /// one with that name, or failing that one showing it too. Null when there is none.
        /// </summary>
        /// <remarks>
        /// A blank display name is left alone: the wrapper passes it on empty, and what Windows
        /// makes of that is the ID's check. One that is the ID over again is checked against
        /// the other services' display names only; a service named like it is the ID's clash,
        /// reported once, there.
        /// </remarks>
        public InstalledService? ClashWithDisplayName(string? displayName, string id)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            return string.Equals(displayName.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)
                ? this.ShownAs(displayName)
                : this.ClashWithId(displayName) ?? this.ShownAs(displayName);
        }

        private static string DisplayNameOf(ServiceController controller)
        {
            try
            {
                return controller.DisplayName ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                // Deleted since the list was taken; its name is still the one to avoid.
                return string.Empty;
            }
        }
    }
}
