using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
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

        public RemoteServiceStatus(string serviceName, string displayName, ServiceControllerStatus? status, string? error, bool? isWrapper = null)
        {
            this.ServiceName = serviceName;
            this.displayName = displayName;
            this.status = status;
            this.error = error;
            this.IsWrapper = isWrapper;
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
                    this.Raise(nameof(this.StatusText));
                    this.Raise(nameof(this.IsRunning));
                    this.Raise(nameof(this.CanStart));
                    this.Raise(nameof(this.CanStop));
                }
            }
        }

        /// <summary>
        /// The status in the interface language, for the pill. <see cref="Status"/> itself would
        /// show the enumeration's own English name, StartPending and all, in every language.
        /// </summary>
        public string StatusText => Localizer.Get(StatusKey(this.status));

        /// <summary>
        /// Whether a WinSW wrapper hosts the service, told from how it was registered; see
        /// <see cref="RemoteMonitor.LooksLikeWrapper"/>. Null when that could not be read, and
        /// on a row from a poll that was not asked because the row on screen already knows.
        /// </summary>
        public bool? IsWrapper { get; private set; }

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

            // A poll does not ask again what the connect already found out.
            if (newer.IsWrapper != null)
            {
                this.IsWrapper = newer.IsWrapper;
            }
        }

        /// <summary>Re-evaluates the localized text after a language change.</summary>
        public void RefreshLocalized() => this.Raise(nameof(this.StatusText));

        /// <summary>
        /// The dictionary key for a status: the same words the Services page uses. A service that
        /// could not be queried has no status and reads as unknown.
        /// </summary>
        internal static string StatusKey(ServiceControllerStatus? status) => status switch
        {
            ServiceControllerStatus.Running => "M.Status.Running",
            ServiceControllerStatus.Stopped => "M.Status.Stopped",
            ServiceControllerStatus.StartPending => "M.Status.Starting",
            ServiceControllerStatus.StopPending => "M.Status.Stopping",
            ServiceControllerStatus.PausePending => "M.Status.Pausing",
            ServiceControllerStatus.ContinuePending => "M.Status.Resuming",
            ServiceControllerStatus.Paused => "M.Status.Paused",
            _ => "M.Status.Unknown",
        };
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

        /// <summary>
        /// The dictionary key naming <paramref name="action"/> in a message: the words on the
        /// buttons that start it.
        /// </summary>
        internal static string VerbKey(RemoteAction action) => action switch
        {
            RemoteAction.Start => "S.Start",
            RemoteAction.Stop => "S.Stop",
            RemoteAction.Restart => "S.Restart",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

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

        /// <summary>Every service on <paramref name="machine"/>, sorted by display name.</summary>
        /// <param name="machine">The computer, as the service control manager takes it.</param>
        /// <param name="classified">
        /// Services not to ask about, because the rows on screen already know: none on a
        /// connect, every row on a poll. These come back with
        /// <see cref="RemoteServiceStatus.IsWrapper"/> null. Asking costs round trips to the
        /// other machine for every service, so a connect asks once for each and a poll only for
        /// a service that has appeared since.
        /// </param>
        /// <exception cref="InvalidOperationException">The machine cannot be reached or refuses the query.</exception>
        public static IReadOnlyList<RemoteServiceStatus> List(string machine, ImmutableHashSet<string> classified)
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

            using var commandLines = new CommandLineReader(machine);
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

                    bool? isWrapper = null;
                    if (!classified.Contains(name) && commandLines.Read(name) is { } binaryPath)
                    {
                        isWrapper = LooksLikeWrapper(binaryPath);
                    }

                    try
                    {
                        results.Add(new RemoteServiceStatus(name, display, service.Status, null, isWrapper));
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        results.Add(new RemoteServiceStatus(name, display, null, e.Message, isWrapper));
                    }
                }
            }

            results.Sort(static (x, y) => string.Compare(x.DisplayName, y.DisplayName, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        /// <summary>
        /// Whether a service registered as <paramref name="binaryPath"/> is hosted by a WinSW
        /// wrapper, told from the command line alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the shape <see cref="ServiceDiscovery"/> checks before it pays for a version
        /// read (its <c>TryDescribe</c>): the wrapper registers itself, an executable, followed
        /// by nothing or by its configuration file. Split the same way, with
        /// <see cref="ServiceDiscovery.SplitCommandLine"/>.
        /// </para>
        /// <para>
        /// What the shape cannot do on its own is rule out an ordinary program registered bare.
        /// On this machine the file's version information settles it; on another one, reading
        /// it means opening that machine's administrative share, and a console that reaches into
        /// C$ on every connect is what intrusion monitoring is there to flag. Windows' own
        /// services, nearly all of the bare ones, are ruled out by where they live instead: a
        /// wrapper registers its full path, and is never installed under the Windows directory.
        /// A third-party program registered bare elsewhere still passes, as it passes the shape
        /// on this machine before the version read rules it out.
        /// </para>
        /// </remarks>
        internal static bool LooksLikeWrapper(string binaryPath)
        {
            var tokens = ServiceDiscovery.SplitCommandLine(binaryPath);
            if (tokens.Count is 0 or > 2)
            {
                return false;
            }

            string program = tokens[0];
            if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (tokens.Count == 2 && !tokens[1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // A full path on a drive: not %SystemRoot%\..., which the wrapper never registers.
            bool fullPath = program.Length > 3 && char.IsAsciiLetter(program[0]) && program[1] == ':' && program[2] == '\\';
            return fullPath && !program.AsSpan(3).StartsWith(@"Windows\", StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryServiceConfigW(IntPtr service, IntPtr buffer, int bufferSize, out int bytesNeeded);

        /// <summary>
        /// The command lines services on one machine are registered with, read through the
        /// service control manager: the channel the listing already uses, asked with the right
        /// Windows grants alongside reading a service's status. One connection and one buffer
        /// for the whole pass, opened on first use: the connection is a round trip of its own.
        /// </summary>
        private sealed class CommandLineReader : IDisposable
        {
            /// <summary>SERVICE_QUERY_CONFIG.</summary>
            private const int ServiceQueryConfig = 0x0001;

            /// <summary>The most QUERY_SERVICE_CONFIG takes with its strings, by its documentation.</summary>
            private const int MaxConfigBytes = 8 * 1024;

            private readonly string machine;
            private IntPtr manager;
            private IntPtr buffer;
            private bool opened;

            public CommandLineReader(string machine) => this.machine = machine;

            /// <summary>
            /// The service's command line; null when it cannot be read. A connection refused
            /// once is not asked for again, service after service.
            /// </summary>
            public string? Read(string serviceName)
            {
                if (!this.opened)
                {
                    this.opened = true;
                    this.manager = NativeMethods.OpenSCManagerW(this.machine, null, NativeMethods.SC_MANAGER_CONNECT);
                    if (this.manager != IntPtr.Zero)
                    {
                        this.buffer = Marshal.AllocHGlobal(MaxConfigBytes);
                    }
                }

                if (this.manager == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr service = NativeMethods.OpenServiceW(this.manager, serviceName, ServiceQueryConfig);
                if (service == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return QueryServiceConfigW(service, this.buffer, MaxConfigBytes, out _)
                        ? Marshal.PtrToStringUni(Marshal.PtrToStructure<ServiceConfig>(this.buffer).BinaryPathName)
                        : null;
                }
                finally
                {
                    NativeMethods.CloseServiceHandle(service);
                }
            }

            public void Dispose()
            {
                if (this.buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(this.buffer);
                    this.buffer = IntPtr.Zero;
                }

                if (this.manager != IntPtr.Zero)
                {
                    NativeMethods.CloseServiceHandle(this.manager);
                    this.manager = IntPtr.Zero;
                }
            }
        }

        /// <summary>QUERY_SERVICE_CONFIGW; only the command line is read.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceConfig
        {
            public int ServiceType;
            public int StartType;
            public int ErrorControl;
            public IntPtr BinaryPathName;
            public IntPtr LoadOrderGroup;
            public int TagId;
            public IntPtr Dependencies;
            public IntPtr ServiceStartName;
            public IntPtr DisplayName;
        }
    }
}
