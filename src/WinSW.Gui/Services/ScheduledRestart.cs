using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace WinSW.Gui.Services
{
    /// <summary>When a service is restarted: every day, or on one day of the week, at a time of day.</summary>
    /// <param name="Day">The day of the week, or null for every day.</param>
    /// <param name="At">The time of day, local.</param>
    public readonly record struct RestartSchedule(DayOfWeek? Day, TimeSpan At);

    /// <summary>
    /// Restarts a service on a schedule, through a task in the task scheduler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A program that leaks, or that holds a connection it never renews, is kept healthy by
    /// being restarted at night. The wrapper has no schedule of its own and the console is
    /// not always running, so the task scheduler does it: a task that runs as SYSTEM, at the
    /// chosen time, whether or not anybody is signed in.
    /// </para>
    /// <para>
    /// Its action is not a bare <c>winsw restart</c>. That command starts a service which was
    /// not running — it treats "not active" as nothing to stop — and a service somebody has
    /// stopped on purpose must not come back at three in the morning. The action asks the
    /// wrapper's own <c>status</c> first and restarts only a running service. Nor is it forced:
    /// a service others depend on is refused by the wrapper, the task records the failure in
    /// its history, and the service stays up, which is how the console's own Restart behaves.
    /// </para>
    /// <para>
    /// A task that runs as SYSTEM can only be registered with administrator rights, so the
    /// definition is written to a file and handed to an elevated <c>schtasks</c>, under the
    /// usual single prompt. Its security descriptor lets any signed-in account read it, which
    /// is what lets the console show the schedule without elevation; running, changing and
    /// deleting it stay with administrators.
    /// </para>
    /// </remarks>
    public static class ScheduledRestart
    {
        /// <summary>
        /// One level below the desktop tasks' folder. <see cref="DesktopTasks.List"/> reads that
        /// folder's own tasks, and these must not appear among them.
        /// </summary>
        internal const string FolderPath = "\\" + DesktopTasks.FolderName + "\\Restart";

        /// <summary>Administrators and SYSTEM in full; any signed-in account may read.</summary>
        internal const string SecurityDescriptor = "D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FR;;;AU)";

        private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        /// <summary>The task's full path in the task scheduler library.</summary>
        public static string TaskPath(string serviceId) => FolderPath + "\\" + serviceId;

        /// <summary>
        /// The schedule registered for <paramref name="serviceId"/>, or null when there is none.
        /// </summary>
        /// <exception cref="UnauthorizedAccessException">The task exists but this account may not read it.</exception>
        /// <exception cref="System.Runtime.InteropServices.COMException">The task scheduler could not be asked.</exception>
        public static RestartSchedule? Read(string serviceId)
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
                folder = service.GetFolder(FolderPath);
                task = ((dynamic)folder).GetTask(serviceId);
                string xml = ((dynamic)task).Xml;
                return Parse(xml);
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

        /// <summary>
        /// Registers the schedule, replacing any before it, with one elevation prompt.
        /// </summary>
        public static async System.Threading.Tasks.Task<CommandResult> SetAsync(string serviceId, string wrapper, string configPath, RestartSchedule schedule)
        {
            // The action runs through cmd, which rewrites %NAME% even inside quotes.
            if (WinSwCli.RejectExpandablePaths(new[] { wrapper, configPath }) is { } refusal)
            {
                return refusal;
            }

            string folder = Path.Combine(Path.GetTempPath(), "WinSW.Gui");
            string file = Path.Combine(folder, Path.GetRandomFileName() + ".xml");
            try
            {
                Directory.CreateDirectory(folder);

                // schtasks reads a definition as UTF-16, which is also what it writes one as.
                File.WriteAllText(file, BuildXml(serviceId, wrapper, configPath, schedule, DateTime.Today), Encoding.Unicode);

                return await WinSwCli.ScheduleTaskAsync(TaskPath(serviceId), file).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return CommandResult.Failed(e.Message);
            }
            finally
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>Removes the schedule, with one elevation prompt.</summary>
        public static System.Threading.Tasks.Task<CommandResult> RemoveAsync(string serviceId) =>
            WinSwCli.UnscheduleTaskAsync(TaskPath(serviceId));

        /// <summary>
        /// The command line the task runs: restart the service if, and only if, it is running.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The wrapper is asked, not <c>sc query</c>: what <c>sc</c> prints can follow the
        /// language of Windows, and what the wrapper prints cannot — its status line is fixed
        /// English, "Active (running)" from 3.x and "Started" from 2.x. <c>findstr</c> is
        /// case-sensitive, so 3.x's "Active (starting)" does not pass for the second.
        /// </para>
        /// <para>
        /// The system's own programs are named by their full path. This runs as SYSTEM, and
        /// whatever the PATH finds first under a bare name would run as SYSTEM with it.
        /// </para>
        /// </remarks>
        internal static string BuildArguments(string wrapper, string configPath) =>
            $"/d /c \"\"{wrapper}\" status \"{configPath}\" | {FindStr} /C:\"Active (running)\" /C:\"Started\" >nul && \"{wrapper}\" restart \"{configPath}\" --no-elevate\"";

        /// <summary>Expanded by the task scheduler, or failing that by cmd; either gives the system directory.</summary>
        internal const string Cmd = @"%SystemRoot%\System32\cmd.exe";

        private const string FindStr = @"%SystemRoot%\System32\findstr.exe";

        internal static string BuildXml(string serviceId, string wrapper, string configPath, RestartSchedule schedule, DateTime today)
        {
            XElement when = schedule.Day is DayOfWeek day
                ? new XElement(
                    TaskNs + "ScheduleByWeek",
                    new XElement(TaskNs + "WeeksInterval", 1),
                    new XElement(TaskNs + "DaysOfWeek", new XElement(TaskNs + day.ToString())))
                : new XElement(
                    TaskNs + "ScheduleByDay",
                    new XElement(TaskNs + "DaysInterval", 1));

            var task = new XElement(
                TaskNs + "Task",
                new XAttribute("version", "1.2"),
                new XElement(
                    TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Description", $"Restarts the WinSW service '{serviceId}' if it is running. Created by the WinSW console."),
                    new XElement(TaskNs + "SecurityDescriptor", SecurityDescriptor)),
                new XElement(
                    TaskNs + "Triggers",
                    new XElement(
                        TaskNs + "CalendarTrigger",
                        new XElement(TaskNs + "StartBoundary", (today.Date + schedule.At).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                        new XElement(TaskNs + "Enabled", "true"),
                        when)),
                new XElement(
                    TaskNs + "Principals",
                    new XElement(
                        TaskNs + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", "S-1-5-18"),
                        new XElement(TaskNs + "RunLevel", "HighestAvailable"))),
                new XElement(
                    TaskNs + "Settings",
                    new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
                    new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),

                    // A restart that was due while the machine was off is not made up at boot:
                    // the service has just started anyway.
                    new XElement(TaskNs + "StartWhenAvailable", "false"),
                    new XElement(TaskNs + "ExecutionTimeLimit", "PT1H"),
                    new XElement(TaskNs + "Enabled", "true")),
                new XElement(
                    TaskNs + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(
                        TaskNs + "Exec",
                        new XElement(TaskNs + "Command", Cmd),
                        new XElement(TaskNs + "Arguments", BuildArguments(wrapper, configPath)))));

            return new XDeclaration("1.0", "UTF-16", null) + Environment.NewLine + task;
        }

        /// <summary>
        /// Reads back a definition this console wrote. Anything else — a trigger edited by hand
        /// into a shape the picker cannot show — is reported as no schedule rather than guessed.
        /// </summary>
        internal static RestartSchedule? Parse(string xml)
        {
            var trigger = XDocument.Parse(xml).Descendants(TaskNs + "CalendarTrigger").FirstOrDefault();
            if (trigger is null
                || !DateTime.TryParse(trigger.Element(TaskNs + "StartBoundary")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                return null;
            }

            var at = new TimeSpan(start.Hour, start.Minute, 0);

            if (trigger.Element(TaskNs + "ScheduleByDay") is not null)
            {
                return new RestartSchedule(null, at);
            }

            var days = trigger.Element(TaskNs + "ScheduleByWeek")?.Element(TaskNs + "DaysOfWeek")?.Elements().ToList();
            return days is { Count: 1 } && Enum.TryParse<DayOfWeek>(days[0].Name.LocalName, out var day)
                ? new RestartSchedule(day, at)
                : null;
        }

        /// <summary>Parses a time of day typed as H:mm or HH:mm.</summary>
        public static bool TryParseTime(string text, out TimeSpan at) =>
            TimeSpan.TryParseExact(text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out at)
            && at >= TimeSpan.Zero && at < TimeSpan.FromDays(1);
    }
}
