using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>What a registered desktop task is doing right now.</summary>
    /// <remarks>Values are the task scheduler's own TASK_STATE constants.</remarks>
    public enum DesktopTaskState
    {
        Unknown = 0,
        Disabled = 1,
        Queued = 2,
        Ready = 3,
        Running = 4,
    }

    /// <summary>Everything needed to register one desktop task; see <see cref="DesktopTasks"/>.</summary>
    public sealed class DesktopTaskPlan
    {
        public DesktopTaskPlan(string id, string wrapperPath, string configPath)
        {
            this.Id = id;
            this.WrapperPath = wrapperPath;
            this.ConfigPath = configPath;
        }

        /// <summary>The service ID from the configuration; also the task's name.</summary>
        public string Id { get; }

        public string WrapperPath { get; }

        public string ConfigPath { get; }

        public string Description { get; init; } = string.Empty;

        /// <summary>The account the task runs as, <c>DOMAIN\user</c>. Defaults to the current user.</summary>
        public string UserId { get; init; } = CurrentUser;

        /// <summary>Run with the account's full token, so a program that needs administrator rights gets them without a prompt.</summary>
        public bool RunElevated { get; init; }

        /// <summary>How long after logon to wait before starting. A desktop that is still settling is a bad one to automate.</summary>
        public TimeSpan LogonDelay { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>Re-run the trigger periodically so that a program which has died comes back.</summary>
        public bool KeepAlive { get; init; } = true;

        public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromMinutes(1);

        public static string CurrentUser
        {
            get
            {
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    return identity.Name;
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
                {
                    return Environment.UserDomainName + "\\" + Environment.UserName;
                }
            }
        }

        /// <summary>The command line the task scheduler runs. See the wrapper's <c>console</c> command.</summary>
        public string Arguments => DesktopTasks.BuildArguments(this.ConfigPath);
    }

    /// <summary>One registered desktop task, as read back from the task scheduler.</summary>
    public sealed class DesktopTaskInfo
    {
        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string WrapperPath { get; init; } = string.Empty;

        public string ConfigPath { get; init; } = string.Empty;

        public string UserId { get; init; } = string.Empty;

        public bool RunElevated { get; init; }

        public bool Enabled { get; init; }

        public DesktopTaskState State { get; init; }

        public DateTime? LastRun { get; init; }

        public int LastResult { get; init; }
    }

    /// <summary>
    /// Registers and drives the scheduled tasks that host a program with a user interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Windows service runs in session 0, which has no desktop: nothing it starts can show a
    /// window, take a keystroke or read the screen, which is the whole job of an automation
    /// robot. The way to run such a program unattended is a scheduled task with a logon
    /// trigger — it starts in the session the user actually logged on to.
    /// </para>
    /// <para>
    /// What the task starts is still the wrapper, in its <c>console</c> mode, so the same
    /// configuration file, the same log rotation and the same child supervision apply. The
    /// task scheduler contributes what the service control manager would have: starting at
    /// logon, and restarting the program if it dies.
    /// </para>
    /// <para>
    /// The task scheduler is reached through its automation object rather than through
    /// <c>schtasks.exe</c>: that tool's output is translated into the display language, so
    /// reading a task's state back from it would break the moment the machine is not English.
    /// </para>
    /// </remarks>
    public static class DesktopTasks
    {
        /// <summary>The task scheduler library folder holding every task this application owns.</summary>
        public const string FolderName = "WinSW";

        private const string FolderPath = "\\" + FolderName;

        // TASK_CREATE_OR_UPDATE
        private const int CreateOrUpdate = 6;

        // TASK_LOGON_INTERACTIVE_TOKEN: run in the session the user is logged on to, no password stored.
        private const int InteractiveToken = 3;

        // TASK_TRIGGER_LOGON / TASK_ACTION_EXEC
        private const int LogonTrigger = 9;
        private const int ExecAction = 0;

        // TASK_INSTANCES_IGNORE_NEW: a keep-alive tick while the program is up does nothing.
        private const int IgnoreNewInstance = 2;

        // TASK_RUNLEVEL_LUA / TASK_RUNLEVEL_HIGHEST
        private const int LeastPrivilege = 0;
        private const int HighestAvailable = 1;

        private const int ErrorFileNotFound = unchecked((int)0x80070002);
        private const int ErrorPathNotFound = unchecked((int)0x80070003);

        private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        /// <summary>False on a machine whose task scheduler automation object is not registered.</summary>
        public static bool IsAvailable => Type.GetTypeFromProgID("Schedule.Service") != null;

        /// <summary>The command line that runs one configuration in the current session.</summary>
        public static string BuildArguments(string configPath) => "console \"" + configPath + "\"";

        /// <summary>
        /// Recovers the configuration path from an argument string produced by
        /// <see cref="BuildArguments"/>, tolerating a task someone edited by hand.
        /// </summary>
        internal static string? ConfigPathFromArguments(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return null;
            }

            var tokens = ServiceDiscovery.SplitCommandLine(arguments!);
            if (tokens.Count == 0 || !tokens[0].Equals("console", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (string token in tokens)
            {
                if (token.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    return token;
                }
            }

            return null;
        }

        /// <summary>Every task registered under <see cref="FolderName"/>, in name order.</summary>
        public static IReadOnlyList<DesktopTaskInfo> List()
        {
            var results = new List<DesktopTaskInfo>();

            object? connection = Connect();
            if (connection is null)
            {
                return results;
            }

            try
            {
                dynamic service = connection;
                dynamic folder = service.GetFolder(FolderPath);

                // 1 is TASK_ENUM_HIDDEN: a task someone marked hidden is still ours to show.
                // The collection is walked by index rather than with foreach, because how a
                // late-bound COM collection presents itself to an enumerator is not something
                // to rely on, while Count and Item are plain dispatch members.
                dynamic tasks = folder.GetTasks(1);
                int count = (int)tasks.Count;
                for (int i = 1; i <= count; i++)
                {
                    object task = tasks.Item(i);
                    if (Describe(task) is { } info)
                    {
                        results.Add(info);
                    }
                }
            }
            catch (Exception e) when (IsMissing(e))
            {
                // Nothing has been registered yet.
            }
            finally
            {
                Release(connection);
            }

            results.Sort(static (x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
            return results;
        }

        public static DesktopTaskInfo? Find(string name)
        {
            object? connection = Connect();
            if (connection is null)
            {
                return null;
            }

            try
            {
                dynamic service = connection;
                dynamic folder = service.GetFolder(FolderPath);
                object task = folder.GetTask(name);
                return Describe(task);
            }
            catch (Exception e) when (IsMissing(e))
            {
                return null;
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>
        /// Creates the task, or replaces the one already registered under the same name.
        /// </summary>
        /// <exception cref="InvalidOperationException">The task scheduler is unreachable.</exception>
        /// <exception cref="COMException">The task scheduler refused the registration.</exception>
        public static void Register(DesktopTaskPlan plan)
        {
            object connection = Connect() ?? throw Unavailable();

            try
            {
                dynamic service = connection;
                dynamic folder = OpenOrCreateFolder(service);
                dynamic definition = service.NewTask(0);

                dynamic registration = definition.RegistrationInfo;
                registration.Author = "WinSW";
                registration.Description = plan.Description;

                dynamic trigger = definition.Triggers.Create(LogonTrigger);
                trigger.Id = "AtLogon";
                trigger.Enabled = true;
                trigger.UserId = plan.UserId;
                trigger.Delay = Duration(plan.LogonDelay);

                if (plan.KeepAlive)
                {
                    // The trigger fires again on a timer; a tick that finds the program already
                    // up is dropped by the multiple-instances policy below, and one that finds it
                    // gone starts it again. This is what stands in for the recovery actions a
                    // service would get from the service control manager.
                    dynamic repetition = trigger.Repetition;
                    repetition.Interval = Duration(plan.KeepAliveInterval);
                    repetition.StopAtDurationEnd = false;
                }

                dynamic principal = definition.Principal;
                principal.Id = "Author";
                principal.UserId = plan.UserId;
                principal.LogonType = InteractiveToken;
                principal.RunLevel = plan.RunElevated ? HighestAvailable : LeastPrivilege;

                dynamic settings = definition.Settings;
                settings.Enabled = true;
                settings.Hidden = false;
                settings.AllowDemandStart = true;
                settings.AllowHardTerminate = true;
                settings.StartWhenAvailable = true;
                settings.RunOnlyIfNetworkAvailable = false;
                settings.DisallowStartIfOnBatteries = false;
                settings.StopIfGoingOnBatteries = false;
                settings.RunOnlyIfIdle = false;
                settings.WakeToRun = false;
                settings.MultipleInstances = IgnoreNewInstance;

                // The default is three days, after which the task scheduler would terminate a
                // program that is meant to run for as long as the user stays logged on.
                settings.ExecutionTimeLimit = "PT0S";

                dynamic idle = settings.IdleSettings;
                idle.StopOnIdleEnd = false;
                idle.RestartOnIdle = false;

                dynamic actions = definition.Actions;

                // Which principal the actions run under. It is the id set above, and leaving
                // it unset is one of the ways a registration is refused.
                actions.Context = "Author";

                dynamic action = actions.Create(ExecAction);
                action.Path = plan.WrapperPath;
                action.Arguments = plan.Arguments;
                action.WorkingDirectory = Path.GetDirectoryName(plan.ConfigPath) ?? string.Empty;

                _ = folder.RegisterTaskDefinition(plan.Id, definition, CreateOrUpdate, plan.UserId, null, InteractiveToken, null);
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>Removes the task. The configuration and its logs are left where they are.</summary>
        public static void Delete(string name)
        {
            object connection = Connect() ?? throw Unavailable();

            try
            {
                dynamic service = connection;
                dynamic folder = service.GetFolder(FolderPath);
                folder.DeleteTask(name, 0);
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>Starts the task now, in the session it is registered for.</summary>
        public static void Start(string name)
        {
            object connection = Connect() ?? throw Unavailable();

            try
            {
                dynamic service = connection;
                dynamic task = service.GetFolder(FolderPath).GetTask(name);
                _ = task.Run(null);
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>Turns the trigger on or off without removing the task.</summary>
        public static void SetEnabled(string name, bool enabled)
        {
            object connection = Connect() ?? throw Unavailable();

            try
            {
                dynamic service = connection;
                dynamic task = service.GetFolder(FolderPath).GetTask(name);
                task.Enabled = enabled;
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>
        /// Ends the task. The wrapper is asked to shut its child down cleanly first, the way a
        /// service stop would; the task scheduler's own termination is the fallback, and that
        /// kills the whole tree without running any stop hook.
        /// </summary>
        /// <param name="name">The registered task name.</param>
        /// <param name="serviceId">The configuration's service ID, which is what the wrapper publishes its stop event under.</param>
        /// <param name="graceTimeout">How long to let a clean shutdown take before terminating.</param>
        public static void Stop(string name, string serviceId, TimeSpan graceTimeout)
        {
            if (RequestStop(serviceId))
            {
                var deadline = DateTime.UtcNow + graceTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    if (Find(name) is not { State: DesktopTaskState.Running })
                    {
                        return;
                    }
                }
            }

            object connection = Connect() ?? throw Unavailable();

            try
            {
                dynamic service = connection;
                dynamic task = service.GetFolder(FolderPath).GetTask(name);
                task.Stop(0);
            }
            finally
            {
                Release(connection);
            }
        }

        /// <summary>
        /// Signals a console-mode wrapper to shut down.
        /// </summary>
        /// <remarks>
        /// This mirrors <c>WinSW.ConsoleSession</c> in the wrapper, which this project does not
        /// reference; the two must be changed together. False means no wrapper is listening in
        /// this session — it was never started, it has already stopped, or it runs elevated
        /// while this process does not.
        /// </remarks>
        public static bool RequestStop(string serviceId)
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(StopEventName(serviceId), out var handle))
                {
                    return false;
                }

                using (handle)
                {
                    return handle.Set();
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        /// <summary>The session-scoped event a console-mode wrapper waits on.</summary>
        internal static string StopEventName(string serviceId)
        {
            var builder = new StringBuilder(@"Local\WinSW.Console.", 64);

            int length = Math.Min(serviceId.Length, 100);
            for (int i = 0; i < length; i++)
            {
                char c = serviceId[i];
                builder.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_' ? c : '_');
            }

            return builder.Append('.').Append(Hash(serviceId).ToString("x8", CultureInfo.InvariantCulture)).ToString();
        }

        /// <summary>FNV-1a; must match the wrapper's.</summary>
        private static uint Hash(string value)
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }

        /// <summary>An ISO 8601 duration, which is how the task scheduler spells a timespan.</summary>
        internal static string Duration(TimeSpan value)
        {
            if (value <= TimeSpan.Zero)
            {
                return "PT0S";
            }

            var builder = new StringBuilder("P");
            if (value.Days > 0)
            {
                builder.Append(value.Days.ToString(CultureInfo.InvariantCulture)).Append('D');
            }

            builder.Append('T');
            if (value.Hours > 0)
            {
                builder.Append(value.Hours.ToString(CultureInfo.InvariantCulture)).Append('H');
            }

            if (value.Minutes > 0)
            {
                builder.Append(value.Minutes.ToString(CultureInfo.InvariantCulture)).Append('M');
            }

            if (value.Seconds > 0 || builder.Length == 2)
            {
                builder.Append(value.Seconds.ToString(CultureInfo.InvariantCulture)).Append('S');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads one registered task. The definition is taken from its XML rather than from the
        /// object model: the document is a documented, language-independent format, and reading
        /// it needs no assumptions about how a late-bound collection indexes.
        /// </summary>
        private static DesktopTaskInfo? Describe(object registered)
        {
            dynamic task = registered;
            string name;
            string xml;
            bool enabled;
            int state;
            int lastResult;
            DateTime? lastRun;

            try
            {
                name = (string)task.Name;
                xml = (string)task.Xml;
                enabled = (bool)task.Enabled;
                state = (int)task.State;
                lastResult = (int)task.LastTaskResult;

                DateTime run = (DateTime)task.LastRunTime;
                lastRun = run.Year < 1980 ? null : (DateTime?)run;
            }
            catch (Exception e) when (IsComFailure(e) || e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                return null;
            }

            var parsed = ParseDefinition(xml);
            if (parsed.ConfigPath is null)
            {
                // Something else was filed under this folder. It is not ours to show, and
                // certainly not ours to stop.
                return null;
            }

            return new DesktopTaskInfo
            {
                Name = name,
                Description = parsed.Description,
                WrapperPath = parsed.Command,
                ConfigPath = parsed.ConfigPath ?? string.Empty,
                UserId = parsed.UserId,
                RunElevated = parsed.RunElevated,
                Enabled = enabled,
                State = (DesktopTaskState)state,
                LastRun = lastRun,
                LastResult = lastResult,
            };
        }

        /// <summary>The parts of a task document this application cares about.</summary>
        internal readonly struct TaskDefinitionSummary
        {
            public TaskDefinitionSummary(string command, string? configPath, string description, string userId, bool runElevated)
            {
                this.Command = command;
                this.ConfigPath = configPath;
                this.Description = description;
                this.UserId = userId;
                this.RunElevated = runElevated;
            }

            public string Command { get; }

            public string? ConfigPath { get; }

            public string Description { get; }

            public string UserId { get; }

            public bool RunElevated { get; }
        }

        private static readonly TaskDefinitionSummary EmptyDefinition =
            new(string.Empty, null, string.Empty, string.Empty, false);

        internal static TaskDefinitionSummary ParseDefinition(string xml)
        {
            try
            {
                var root = XDocument.Parse(xml).Root;
                if (root is null)
                {
                    return EmptyDefinition;
                }

                var exec = root.Element(TaskNs + "Actions")?.Element(TaskNs + "Exec");
                var registration = root.Element(TaskNs + "RegistrationInfo");
                var principal = root.Element(TaskNs + "Principals")?.Element(TaskNs + "Principal");

                string arguments = exec?.Element(TaskNs + "Arguments")?.Value ?? string.Empty;

                return new TaskDefinitionSummary(
                    exec?.Element(TaskNs + "Command")?.Value ?? string.Empty,
                    ConfigPathFromArguments(arguments),
                    registration?.Element(TaskNs + "Description")?.Value ?? string.Empty,
                    principal?.Element(TaskNs + "UserId")?.Value ?? string.Empty,
                    string.Equals(principal?.Element(TaskNs + "RunLevel")?.Value, "HighestAvailable", StringComparison.OrdinalIgnoreCase));
            }
            catch (System.Xml.XmlException)
            {
                return EmptyDefinition;
            }
        }

        /// <summary>
        /// True when a call failed because the thing it named is not there.
        /// </summary>
        /// <remarks>
        /// Deliberately typed as <see cref="Exception"/>. A failing HRESULT does not reach
        /// managed code as a <see cref="COMException"/>: the runtime maps the well-known ones
        /// onto specific types first, and the two that matter here — ERROR_FILE_NOT_FOUND and
        /// ERROR_PATH_NOT_FOUND — arrive as <see cref="FileNotFoundException"/> and
        /// <see cref="DirectoryNotFoundException"/>. The HRESULT is the contract; which
        /// exception class carries it is not.
        /// </remarks>
        internal static bool IsMissing(Exception e) =>
            e.HResult == ErrorFileNotFound || e.HResult == ErrorPathNotFound;

        /// <summary>True for the exception classes a failed late-bound COM call arrives as.</summary>
        private static bool IsComFailure(Exception e) =>
            e is COMException or IOException or UnauthorizedAccessException or InvalidCastException;

        /// <summary>
        /// Opens this application's folder in the task scheduler library, creating it the
        /// first time.
        /// </summary>
        /// <remarks>
        /// A failure to create it is reported rather than worked around. Registering the task
        /// somewhere else instead would succeed and then be invisible: every read here looks
        /// in one folder, and a task outside it is one nothing in this application can find,
        /// stop or remove.
        /// </remarks>
        private static dynamic OpenOrCreateFolder(dynamic service)
        {
            try
            {
                return service.GetFolder(FolderPath);
            }
            catch (Exception e) when (IsMissing(e))
            {
                dynamic root = service.GetFolder("\\");
                try
                {
                    return root.CreateFolder(FolderName, null);
                }
                catch (Exception inner) when (IsComFailure(inner))
                {
                    throw new InvalidOperationException(Localizer.Format("M.Task.NoFolder", FolderName, inner.Message), inner);
                }
            }
        }

        /// <summary>
        /// Creates the task scheduler's automation object and connects it to this machine.
        /// The result is deliberately typed as <see cref="object"/>: a dynamic value passed to
        /// one of the helpers here would make that call late-bound as well, for no reason.
        /// </summary>
        private static object? Connect()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return null;
            }

            object connection = Activator.CreateInstance(type)!;
            dynamic service = connection;
            service.Connect();
            return connection;
        }

        private static void Release(object? service)
        {
            if (service != null && Marshal.IsComObject(service))
            {
                _ = Marshal.ReleaseComObject(service);
            }
        }

        private static InvalidOperationException Unavailable() =>
            new(Localizer.Get("M.Task.Unavailable"));
    }
}
