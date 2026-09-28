using System;
using System.Collections.Generic;
using System.Linq;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The services an operation started from the console is working on right now, by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A stop can wait three minutes, and the list stays usable while it does: the user may well
    /// move on to another service in the meantime. What has to be kept apart is the service being
    /// worked on — a Terminate or an Uninstall clicked on it halfway through a stop is a second
    /// operation racing the first — and it is kept apart per service, so that the rest of the list
    /// is not held up by one slow stop.
    /// </para>
    /// <para>
    /// An operation holds more than its own service when it acts on more. Stopping a service stops
    /// every service that depends on it first, and upgrading a wrapper stops and restarts every
    /// service running from that file. Ending a stray process and collecting diagnostics hold
    /// nothing: neither changes the state of a service.
    /// </para>
    /// <para>
    /// Counted rather than a plain set: two operations may hold the same service, and the first to
    /// finish must not release it while the other is still at work. Keyed without regard to case,
    /// as the service control manager keys them, and by name rather than by entry, because a
    /// rescan may replace the entry under a running operation.
    /// </para>
    /// </remarks>
    public sealed class OperationsInFlight
    {
        private readonly Dictionary<string, int> held = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when an operation begins or ends; the commands it disables are re-asked then.</summary>
        public event Action? Changed;

        /// <summary>The number of distinct services held.</summary>
        public int Count => this.held.Count;

        /// <summary>
        /// The services <paramref name="command"/> on <paramref name="target"/> works on:
        /// <paramref name="target"/> itself, the services that depend on it for a stop or a
        /// restart, and every service sharing its wrapper file for an upgrade.
        /// </summary>
        /// <param name="command">The wrapper command, as the dashboard names it: "start", "stop", "restart", "refresh", "dev kill", "uninstall" or "upgrade".</param>
        /// <param name="target">The service the command was given for.</param>
        /// <param name="services">Every service on the list, for finding the ones sharing a wrapper.</param>
        /// <remarks>
        /// The dependents are the service control manager's own list, which already includes the
        /// ones that depend on it through another service. Names not on the list — dependents that
        /// are not WinSW services — are harmless to hold: nothing here ever asks about them.
        /// </remarks>
        public static IReadOnlyCollection<string> NamesFor(string command, ServiceEntry target, IEnumerable<ServiceEntry> services)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.ServiceName };
            switch (command)
            {
                // The wrapper stops a service through ServiceController.Stop, which stops each
                // dependent first; a restart starts again the dependents that were running.
                case "stop":
                case "restart":
                    names.UnionWith(target.DependedBy);
                    break;

                // One file, replaced once, with every service running from it stopped around it.
                case "upgrade":
                    names.UnionWith(services
                        .Where(e => string.Equals(e.WrapperPath, target.WrapperPath, StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.ServiceName));
                    break;
            }

            return names;
        }

        /// <summary>True while an operation holds <paramref name="serviceName"/>.</summary>
        public bool Contains(string serviceName) => this.held.ContainsKey(serviceName);

        /// <summary>True while an operation holds any of <paramref name="serviceNames"/>.</summary>
        public bool ContainsAny(IEnumerable<string> serviceNames) => serviceNames.Any(this.Contains);

        /// <summary>
        /// Holds <paramref name="serviceNames"/> until the returned scope is disposed. Disposing it
        /// twice releases once.
        /// </summary>
        public IDisposable Begin(IEnumerable<string> serviceNames)
        {
            var names = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                this.held[name] = this.held.TryGetValue(name, out int count) ? count + 1 : 1;
            }

            this.Changed?.Invoke();
            return new Scope(this, names);
        }

        private void End(IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                if (this.held.TryGetValue(name, out int count))
                {
                    if (count > 1)
                    {
                        this.held[name] = count - 1;
                    }
                    else
                    {
                        this.held.Remove(name);
                    }
                }
            }

            this.Changed?.Invoke();
        }

        private sealed class Scope : IDisposable
        {
            private readonly IReadOnlyCollection<string> names;
            private OperationsInFlight? owner;

            public Scope(OperationsInFlight owner, IReadOnlyCollection<string> names)
            {
                this.owner = owner;
                this.names = names;
            }

            public void Dispose()
            {
                var owner = this.owner;
                this.owner = null;
                owner?.End(this.names);
            }
        }
    }
}
