using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>What a headless run of the executable is for; see <see cref="UnattendedAlert.ParseHeadless"/>.</summary>
    public enum HeadlessAction
    {
        /// <summary>The task scheduler's run: post one failure Windows recorded.</summary>
        Alert,

        /// <summary>The elevated step that turns the unattended alert on.</summary>
        SetUp,

        /// <summary>The elevated step that turns it off.</summary>
        Remove,
    }

    /// <summary>A headless run: what it does, and the one value it was given, if any.</summary>
    public readonly record struct HeadlessCommand(HeadlessAction Action, string? Argument);

    /// <summary>
    /// What the unattended alert was set up with, written beside it where any signed-in account
    /// can read it. It holds nothing secret: the console compares it with its own settings to
    /// say when the copy is out of date.
    /// </summary>
    public sealed class AlertManifest
    {
        /// <summary>The console's version, copied to run the alert.</summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>See <see cref="UnattendedAlert.Fingerprint"/>.</summary>
        public string Fingerprint { get; set; } = string.Empty;

        /// <summary>The language the messages are written in.</summary>
        public string? Language { get; set; }

        public DateTimeOffset InstalledAt { get; set; }

        /// <summary>The account that approved turning it on.</summary>
        public string InstalledBy { get; set; } = string.Empty;
    }

    /// <summary>The unattended alert as a console reads it: whether it is on, what with, and its last message.</summary>
    public sealed record UnattendedAlertStatus(bool Active, AlertManifest? Manifest, AlertOutcome? Last);

    /// <summary>
    /// The webhook as the unattended alert keeps it: address, secret, and the language to
    /// write in. Sealed to the machine, not to a user; see <see cref="UnattendedAlert"/>.
    /// </summary>
    internal sealed class WebhookCopy
    {
        public string Url { get; set; } = string.Empty;

        public string Secret { get; set; } = string.Empty;

        public string? Language { get; set; }
    }

    /// <summary>
    /// Posts a WinSW service's failure to the group chat even when nobody is signed in: a
    /// task in the task scheduler, started by the failure Windows records, running as SYSTEM.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console's own alert comes from its poll, and only while somebody is signed in with
    /// the console running. After a reboot for updates at three in the morning nobody is, and
    /// a service that does not come back is announced to no one. The service control manager
    /// records every such failure in the System log — stopped unexpectedly (7031, 7034), failed
    /// to start (7000), did not start in time (7009), stopped with an error (7023, 7024) — and
    /// the task <c>\WinSW\Alert</c> runs on each of them. The query names no service: it
    /// would have to change with every install. The run looks the service up and stays quiet
    /// about anything that is not a WinSW service.
    /// </para>
    /// <para>
    /// The run is this executable, headless (<c>--alert</c>), handled before the console
    /// claims the session or opens a window. It posts with a copy of the webhook: the console's
    /// own is sealed to the Windows user (<see cref="ProtectedText"/>), which SYSTEM cannot
    /// open. The copy is sealed to the machine instead, and what keeps it from the machine's
    /// other users is the folder: <c>%ProgramData%\WinSW.Gui\Alert</c>, which only
    /// administrators and SYSTEM can open at all.
    /// </para>
    /// <para>
    /// The task runs a copy of the executable kept in that folder, never the one the console
    /// was started from. That one may sit anywhere — Downloads, a desktop — where a standard
    /// user can replace it, and whatever is there would then run as SYSTEM. For the same
    /// reason the copy unpacks its native libraries into that folder too: a self-contained
    /// build unpacks them into its temporary directory, which for SYSTEM is
    /// <c>C:\Windows\Temp</c>, where any user can create the directory first and put libraries
    /// of their own in it. The only way to say where is an environment variable, which the task
    /// scheduler cannot set, so the task runs <c>cmd</c> to set it. Nothing from the event is
    /// put on that command line, which cmd would read as commands, only the record number
    /// (<c>$(EventRecordID)</c>); the run reads the event itself back from the log.
    /// </para>
    /// <para>
    /// Turning it on or off is one elevation prompt: the console starts itself as
    /// administrator with <c>--alert-setup</c> or <c>--alert-remove</c>, and that run prepares
    /// the folders, copies the executable and the webhook, and registers the task with
    /// <c>schtasks /XML</c>, as a scheduled restart does. While the task is there and posts to
    /// the console's own webhook, the console leaves the posting to it; its tray notifications
    /// are unchanged.
    /// </para>
    /// </remarks>
    public static class UnattendedAlert
    {
        /// <summary>The task scheduler's run: <c>--alert &lt;record number&gt;</c>.</summary>
        public const string AlertSwitch = "--alert";

        /// <summary>The elevated run that turns it on: <c>--alert-setup &lt;sealed copy&gt;</c>.</summary>
        public const string SetUpSwitch = "--alert-setup";

        /// <summary>The elevated run that turns it off.</summary>
        public const string RemoveSwitch = "--alert-remove";

        /// <summary>The task's name, in the folder the desktop tasks and scheduled restarts use.</summary>
        internal const string TaskName = "Alert";

        internal const string TaskPath = "\\" + DesktopTasks.FolderName + "\\" + TaskName;

        /// <summary>The service control manager's failures, as the System log numbers them.</summary>
        internal static readonly int[] EventIds = { 7000, 7009, 7023, 7024, 7031, 7034 };

        // Exit codes of the headless runs. The elevated ones can only be read back from the exit
        // code — a runas launch has no output to redirect — so the reason goes to the error log
        // of the account that approved the prompt, and the code says which kind of failure it was.
        internal const int ExitDone = 0;
        internal const int ExitFailed = 1;
        internal const int ExitBadCopy = 2;
        internal const int ExitNameTaken = 3;
        internal const int ExitFiles = 4;
        internal const int ExitScheduler = 5;
        internal const int ExitNotOneFile = 6;

        /// <summary>The value the task passes; the name is the event's own element.</summary>
        internal const string RecordValue = "EventRecordID";

        /// <summary>Read by the .NET host: where a self-contained build unpacks its native libraries.</summary>
        internal const string ExtractionVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";

        /// <summary>
        /// How long the elevated step may take: it copies the executable, which can be 70 MB,
        /// and may first wait for a run of the task to let go of the old copy
        /// (<see cref="UnattendedAlertSetup.InUseDelays"/>).
        /// </summary>
        internal static readonly TimeSpan ElevatedTimeout = TimeSpan.FromMinutes(3);

        private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        /// <summary>Not the settings entropy: this copy is sealed differently and must not open as that.</summary>
        private static readonly byte[] CopyEntropy = Encoding.UTF8.GetBytes("WinSW.Gui unattended alert");

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        /// <summary>
        /// <c>%ProgramData%\WinSW.Gui</c>: administrators and SYSTEM may change it, any signed-in
        /// account may read it. The state, the log and the manifest are here.
        /// </summary>
        public static string MachineFolder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinSW.Gui");

        /// <summary>Administrators and SYSTEM only: the webhook copy, the executable and what it unpacks.</summary>
        internal static string PrivateFolder { get; } = Path.Combine(MachineFolder, "Alert");

        internal static string RunnerPath { get; } = Path.Combine(PrivateFolder, "WinSW.Gui.exe");

        internal static string CopyPath { get; } = Path.Combine(PrivateFolder, "webhook.dat");

        internal static string ExtractionBase { get; } = Path.Combine(PrivateFolder, "runtime");

        internal static string ManifestPath { get; } = Path.Combine(MachineFolder, "alert.json");

        internal static string StatePath { get; } = Path.Combine(MachineFolder, "alert-state.json");

        /// <summary>One line per message the task attempted or held back, as the action log keeps them.</summary>
        public static string LogPath { get; } = Path.Combine(MachineFolder, "alerts.log");

        // Headless runs ------------------------------------------------------------------

        /// <summary>
        /// The headless run the command line asks for, or null for the console. Only the first
        /// argument is looked at, where the task and the console put the switch; anything after
        /// it is the switch's, however it looks.
        /// </summary>
        public static HeadlessCommand? ParseHeadless(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                return null;
            }

            string? argument = args.Count > 1 ? args[1] : null;
            return args[0].ToLowerInvariant() switch
            {
                AlertSwitch => new HeadlessCommand(HeadlessAction.Alert, argument),
                SetUpSwitch => new HeadlessCommand(HeadlessAction.SetUp, argument),
                RemoveSwitch => new HeadlessCommand(HeadlessAction.Remove, null),
                _ => null,
            };
        }

        /// <summary>
        /// Runs a headless command to the end and returns the process's exit code. Must be
        /// called on the application's thread, before anything else starts.
        /// </summary>
        /// <remarks>
        /// Nothing may escape as an unhandled exception or a dialog. The task's run is in
        /// session 0, where a dialog waits for nobody until the task scheduler ends the run;
        /// what went wrong goes to the error log instead, and for the task's run to
        /// <see cref="LogPath"/> as well.
        /// </remarks>
        public static int RunHeadless(HeadlessCommand command)
        {
            try
            {
                return command.Action switch
                {
                    HeadlessAction.Alert => UnattendedAlertRun.Run(command.Argument),
                    HeadlessAction.SetUp => UnattendedAlertSetup.SetUp(command.Argument),
                    _ => UnattendedAlertSetup.Remove(),
                };
            }
            catch (Exception e)
            {
                // The handler of last resort for a process with nobody to tell. The task's
                // error log is SYSTEM's, which nobody looks in; its own log is where it shows.
                ErrorLog.Record("unattended alert, " + command.Action.ToString().ToLowerInvariant(), e);
                if (command.Action == HeadlessAction.Alert)
                {
                    UnattendedAlertRun.Log("-", "failed: " + AlertWebhook.DescribeFailure(e));
                }

                return ExitFailed;
            }
        }

        // The console's side ------------------------------------------------------------

        /// <summary>
        /// Turns the unattended alert on — or, when it is on, brings its copy up to date — with
        /// one elevation prompt.
        /// </summary>
        /// <param name="language">The language the messages are to be written in.</param>
        public static Task<CommandResult> TurnOnAsync(string url, string secret, string language)
        {
            // Before anything else, and before the prompt: the task runs a copy of this
            // executable alone, and a console built from source is an executable beside its
            // assemblies, which the copy would be without. Every run would fail, with the
            // settings page saying the alert is on.
            if (!SelfUpdate.IsSingleFile())
            {
                return Task.FromResult(CommandResult.Failed(Localizer.Get("M.Alert.UnattendedNotOneFile")));
            }

            // Checked here, where the address can still be shown to the person who typed it:
            // a failure in the task's run goes to a log every signed-in account can read.
            try
            {
                AlertWebhook.Build(url.Trim(), secret.Trim(), string.Empty, DateTimeOffset.UtcNow);
            }
            catch (UriFormatException e)
            {
                return Task.FromResult(CommandResult.Failed(e.Message));
            }

            string sealedCopy;
            try
            {
                // Sealed to the machine here, unelevated, which that scope allows: the elevated
                // step is then handed something no more readable than the file it becomes.
                sealedCopy = Convert.ToBase64String(Seal(new WebhookCopy { Url = url.Trim(), Secret = secret.Trim(), Language = language }));
            }
            catch (CryptographicException e)
            {
                return Task.FromResult(CommandResult.Failed(e.Message));
            }

            return RunElevatedAsync(SetUpSwitch, sealedCopy);
        }

        /// <summary>Removes the task and the copy, with one elevation prompt.</summary>
        public static Task<CommandResult> TurnOffAsync() => RunElevatedAsync(RemoveSwitch);

        /// <summary>
        /// True while the task is registered, is this console's, and is enabled. Asks the task
        /// scheduler, so not on the UI thread. Never throws; a scheduler that cannot be asked
        /// reads as off, so that the console posts itself rather than nobody posting.
        /// </summary>
        public static bool IsActive()
        {
            try
            {
                return ReadTask() is { Ours: true, Enabled: true };
            }
            catch (Exception e) when (IsSchedulerFailure(e))
            {
                return false;
            }
        }

        /// <summary>
        /// True while the task is active and posts to <paramref name="url"/> with
        /// <paramref name="secret"/>: only then does the console leave a failure to it. A task
        /// set up with another robot — one since deleted, or another administrator's — would
        /// otherwise take this console's alerts somewhere nobody reads them. Reads the task
        /// scheduler and the manifest, so not on the UI thread; never throws.
        /// </summary>
        public static bool PostsTo(string url, string secret) =>
            IsActive() && SameWebhook(ReadJson<AlertManifest>(ManifestPath), url, secret);

        /// <summary>Whether the task was set up with this address and secret, as its manifest says; false without one.</summary>
        internal static bool SameWebhook(AlertManifest? manifest, string url, string secret) =>
            manifest is not null && string.Equals(manifest.Fingerprint, Fingerprint(url, secret), StringComparison.Ordinal);

        /// <summary>Everything the settings page shows about it. Reads files and the task scheduler; never throws.</summary>
        public static UnattendedAlertStatus ReadStatus() => new(
            IsActive(),
            ReadJson<AlertManifest>(ManifestPath),
            File.Exists(StatePath) ? AlertState.Load(StatePath).Last : null);

        /// <summary>
        /// Whether a stop the console saw is also one the task posts: the service control
        /// manager records a failure only when the service ended with an exit code. A program
        /// that ended with 0 is a clean stop to Windows, and stays the console's to announce.
        /// </summary>
        public static bool CoversStop(int? lastExitCode) => lastExitCode is int code && code != 0;

        /// <summary>
        /// Whether the task posts what <paramref name="notice"/> tells, so that a console whose
        /// webhook it posts to (<see cref="PostsTo"/>) leaves the notice to it. Decided kind by
        /// kind, from what the task can know: each of its runs is started by one failure the
        /// service control manager recorded, and knows nothing else but its throttle's state.
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        /// <item><description>
        /// A crash told at once (<see cref="StopNoticeKind.UnexpectedStop"/>) is the task's:
        /// Windows records it, as 7031 or 7034, and the task posts that record.
        /// </description></item>
        /// <item><description>
        /// The count at the end of a restart loop's window (<see cref="StopNoticeKind.RepeatedStops"/>)
        /// is the task's too, when the loop's last stop was a failure. No one record is a count,
        /// but the task keeps one: its throttle (<see cref="AlertThrottle"/>) holds back every
        /// failure after the first for five minutes and says how many it held in its next
        /// message, and each 7031 carries Windows' own count besides. The console's count on top
        /// of that would tell the same loop twice, minutes apart. What is given up is the count of
        /// a loop that ends inside the window, which the task says only at the next failure; the
        /// console's recovered notice still says the service is running again.
        /// </description></item>
        /// <item><description>
        /// A stop with exit code 0 (<see cref="StopNoticeKind.CleanStop"/>) is the console's:
        /// Windows records no failure for it, and the task never runs.
        /// </description></item>
        /// <item><description>
        /// Running again after a crash (<see cref="StopNoticeKind.Recovered"/>) is the console's:
        /// the task sees failures only, never a service that is running.
        /// </description></item>
        /// <item><description>
        /// Nothing about a desktop task is the task's: a desktop task is run by the task scheduler,
        /// not by the service control manager, and puts nothing in the System log.
        /// </description></item>
        /// </list>
        /// </remarks>
        public static bool Covers(StopNotice notice) =>
            !notice.DesktopTask
            && (notice.Kind is StopNoticeKind.UnexpectedStop or StopNoticeKind.RepeatedStops)
            && CoversStop(notice.ExitCode);

        /// <summary>
        /// A short digest of the address and secret, recorded when the alert is turned on so the
        /// console can tell when its own have changed since. Not the values: the manifest it is
        /// kept in is readable by every signed-in account.
        /// </summary>
        public static string Fingerprint(string url, string secret)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim() + "\n" + secret.Trim()));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        // Pieces shared with the runs ------------------------------------------------------

        /// <summary>
        /// The task's command line: point the .NET host at the private folder, then start the
        /// copy and wait for it, so that the task's queue and time limit apply to the run
        /// itself and its exit code is the task's result. Only a number from the event is
        /// substituted into it.
        /// </summary>
        internal static string BuildArguments(string runner, string extractionBase) =>
            $"/d /c \"set \"{ExtractionVariable}={extractionBase}\" && start \"\" /wait \"{runner}\" {AlertSwitch} $({RecordValue})\"";

        /// <summary>The event log query the task starts on: the six failures, from any service.</summary>
        internal static string BuildQuery() =>
            "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">"
            + "*[System[Provider[@Name='Service Control Manager'] and ("
            + string.Join(" or ", EventIds.Select(id => "EventID=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            + ")]]</Select></Query></QueryList>";

        internal static string BuildXml(string runner, string extractionBase)
        {
            var task = new XElement(
                TaskNs + "Task",
                new XAttribute("version", "1.2"),
                new XElement(
                    TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Description", "Posts to the webhook set in the WinSW console when Windows records that a WinSW service failed, whether or not anybody is signed in. Created by the WinSW console."),
                    new XElement(TaskNs + "SecurityDescriptor", ScheduledRestart.SecurityDescriptor)),
                new XElement(
                    TaskNs + "Triggers",
                    new XElement(
                        TaskNs + "EventTrigger",
                        new XElement(TaskNs + "Enabled", "true"),

                        // A string, so that it is written escaped: the task scheduler reads the
                        // query as text, not as part of the task's own XML.
                        new XElement(TaskNs + "Subscription", BuildQuery()),
                        new XElement(
                            TaskNs + "ValueQueries",
                            new XElement(TaskNs + "Value", new XAttribute("name", RecordValue), "Event/System/EventRecordID")))),
                new XElement(
                    TaskNs + "Principals",
                    new XElement(
                        TaskNs + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", "S-1-5-18"),
                        new XElement(TaskNs + "RunLevel", "HighestAvailable"))),
                new XElement(
                    TaskNs + "Settings",

                    // Failures come in bursts — a crash loop, or several services at boot — and
                    // each is a run. Queued, none is dropped and none runs beside another; the
                    // throttle decides which of them are worth a message.
                    new XElement(TaskNs + "MultipleInstancesPolicy", "Queue"),
                    new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
                    new XElement(TaskNs + "ExecutionTimeLimit", "PT5M"),
                    new XElement(TaskNs + "Enabled", "true")),
                new XElement(
                    TaskNs + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        TaskNs + "Exec",
                        new XElement(TaskNs + "Command", ScheduledRestart.Cmd),
                        new XElement(TaskNs + "Arguments", BuildArguments(runner, extractionBase)))));

            return new XDeclaration("1.0", "UTF-16", null) + Environment.NewLine + task;
        }

        /// <summary>
        /// Whether a task definition is this one's, rather than something else of the same name
        /// — a desktop task somebody called "Alert" — that turning it on or off must not replace.
        /// </summary>
        internal static bool IsOurs(string xml)
        {
            try
            {
                string? arguments = XDocument.Parse(xml).Descendants(TaskNs + "Exec").FirstOrDefault()?.Element(TaskNs + "Arguments")?.Value;
                return arguments?.Contains(" " + AlertSwitch + " ", StringComparison.Ordinal) == true;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// The WinSW service a failure is about, by the name the service control manager logged:
        /// its display name, or its own name when it has none. Null for any other service.
        /// </summary>
        internal static string? FindService(IEnumerable<(string ServiceName, string DisplayName)> services, string loggedName)
        {
            var list = services.ToList();
            foreach (var (serviceName, displayName) in list)
            {
                if (string.Equals(displayName, loggedName, StringComparison.OrdinalIgnoreCase))
                {
                    return serviceName;
                }
            }

            foreach (var (serviceName, _) in list)
            {
                if (string.Equals(serviceName, loggedName, StringComparison.OrdinalIgnoreCase))
                {
                    return serviceName;
                }
            }

            return null;
        }

        internal static string CopyToJson(WebhookCopy copy) => JsonSerializer.Serialize(copy, Json);

        internal static WebhookCopy? CopyFromJson(string json)
        {
            try
            {
                var copy = JsonSerializer.Deserialize<WebhookCopy>(json, Json);
                return copy is { Url.Length: > 0 } ? copy : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static byte[] Seal(WebhookCopy copy) =>
            ProtectedData.Protect(Encoding.UTF8.GetBytes(CopyToJson(copy)), CopyEntropy, DataProtectionScope.LocalMachine);

        /// <summary>Null when it does not open or does not hold a webhook.</summary>
        internal static WebhookCopy? Unseal(byte[] sealedCopy)
        {
            try
            {
                return CopyFromJson(Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedCopy, CopyEntropy, DataProtectionScope.LocalMachine)));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        /// <summary>The copy the task posts with; null when it is missing or does not open.</summary>
        internal static WebhookCopy? ReadCopy()
        {
            try
            {
                return Unseal(File.ReadAllBytes(CopyPath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        internal static string ManifestToJson(AlertManifest manifest) => JsonSerializer.Serialize(manifest, Json);

        internal static T? ReadJson<T>(string path)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The task, if there is one by that name: whether it is this console's, and whether it
        /// is enabled. The security descriptor it is registered with lets a standard user read it.
        /// </summary>
        /// <exception cref="COMException">The task scheduler could not be asked.</exception>
        internal static (bool Ours, bool Enabled)? ReadTask()
        {
            object? connection = DesktopTasks.Connect();
            if (connection is null)
            {
                return null;
            }

            object? folder = null;
            object? task = null;
            try
            {
                dynamic service = connection;
                folder = service.GetFolder("\\" + DesktopTasks.FolderName);
                task = ((dynamic)folder).GetTask(TaskName);
                string xml = ((dynamic)task).Xml;
                bool enabled = ((dynamic)task).Enabled;
                return (IsOurs(xml), enabled);
            }
            catch (Exception e) when (DesktopTasks.IsMissing(e))
            {
                return null;
            }
            finally
            {
                DesktopTasks.Release(task);
                DesktopTasks.Release(folder);
                DesktopTasks.Release(connection);
            }
        }

        /// <summary>The classes a failed late-bound call to the task scheduler arrives as.</summary>
        internal static bool IsSchedulerFailure(Exception e) =>
            e is COMException or IOException or UnauthorizedAccessException or InvalidCastException
                or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

        /// <summary>
        /// Starts this executable as administrator for one of the elevated steps and waits for
        /// it. The steps report only through their exit code, which is turned into a message here.
        /// </summary>
        private static async Task<CommandResult> RunElevatedAsync(params string[] arguments)
        {
            string? path = Environment.ProcessPath;
            if (path is null)
            {
                return CommandResult.Failed(Localizer.Get("M.Cli.CannotStart"));
            }

            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            };

            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(start);
                if (process is null)
                {
                    return CommandResult.Failed(Localizer.Get("M.Cli.CannotStart"));
                }

                using var cancellation = new CancellationTokenSource(ElevatedTimeout);
                try
                {
                    await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new CommandResult(-1, false, true, Localizer.Format("M.Alert.UnattendedTimedOut", (int)ElevatedTimeout.TotalSeconds));
                }

                return process.ExitCode switch
                {
                    ExitDone => CommandResult.Ok(),
                    ExitNameTaken => new CommandResult(ExitNameTaken, false, false, Localizer.Format("M.Alert.UnattendedTaken", TaskPath)),
                    ExitNotOneFile => new CommandResult(ExitNotOneFile, false, false, Localizer.Get("M.Alert.UnattendedNotOneFile")),
                    int code => new CommandResult(code, false, false, Localizer.Format("M.Alert.UnattendedFailed", code)),
                };
            }
            catch (Win32Exception e) when (e.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return new CommandResult(NativeMethods.ERROR_CANCELLED, true, false, null);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
            {
                return CommandResult.Failed(e.Message);
            }
        }
    }
}
