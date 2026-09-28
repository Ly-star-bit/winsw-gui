using System;
using System.Collections.Generic;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>What a <see cref="StopNotice"/> tells.</summary>
    public enum StopNoticeKind
    {
        /// <summary>
        /// The service stopped on its own with a failure exit code, the first crash in a while:
        /// told at once. The dashboard raises this one as its <c>UnexpectedStop</c> event.
        /// </summary>
        UnexpectedStop,

        /// <summary>
        /// The service stopped with exit code 0, outside a restart loop. That is how a program
        /// that ends by itself leaves it, and also how an orderly stop looks when it is over
        /// between two readings, so it is told as a stop, not as a crash, and without a cause:
        /// at the next reading, once the service is seen still stopped rather than starting again.
        /// </summary>
        CleanStop,

        /// <summary>
        /// The service went on stopping after the crash that was told, and something went on
        /// starting it again: told once, when the window ends, with the count.
        /// </summary>
        RepeatedStops,

        /// <summary>
        /// The service has been running for <see cref="CrashAnnouncer.RecoveredAfter"/> since a
        /// crash that was told.
        /// </summary>
        Recovered,
    }

    /// <summary>
    /// One notice about a service's stops, for the tray and the group chat. Plain values: it is
    /// raised on the UI thread and may be sent from any other.
    /// </summary>
    /// <param name="ServiceName">The service's name, as the service control manager knows it.</param>
    /// <param name="Kind">What is being told.</param>
    /// <param name="Count">
    /// For <see cref="StopNoticeKind.RepeatedStops"/>, how many times the service stopped in the
    /// window, the crash told at once included; 1 for a stop told at once; 0 for
    /// <see cref="StopNoticeKind.Recovered"/>.
    /// </param>
    /// <param name="ExitCode">The exit code of the last stop the notice covers.</param>
    public sealed record StopNotice(string ServiceName, StopNoticeKind Kind, int Count, int ExitCode)
    {
        /// <summary>
        /// The notice is about a desktop task, a program the task scheduler runs in the signed-in
        /// session (see <see cref="DesktopTasks"/>), rather than a service. <see cref="ServiceName"/>
        /// is then the task's name, which is its configuration's service ID.
        /// </summary>
        public bool DesktopTask { get; init; }
    }

    /// <summary>
    /// A <see cref="StopNotice"/> in words: the tray balloon, and the first line of the group
    /// chat's message. The format function is <c>Localizer.Format</c> outside the tests, which
    /// have no dictionaries to read.
    /// </summary>
    /// <remarks>
    /// A desktop task's exit with 0 is never told (see <see cref="DesktopTaskWatch"/>): a robot
    /// that finishes its work, or is closed by the person at the desktop, ends that way. Its
    /// notices are the crash, the count and the recovery, worded for a task, which the task
    /// scheduler's keep-alive trigger starts again rather than Windows' service recovery.
    /// </remarks>
    public static class StopNoticeText
    {
        /// <summary>
        /// What a tray balloon about a desktop task is tagged with in front of its name: the
        /// task's folder in the task scheduler. A service name cannot hold a backslash, so the
        /// tag says which of the two the balloon is about.
        /// </summary>
        private const string TaskTagPrefix = "\\" + DesktopTasks.FolderName + "\\";

        /// <summary>The tray balloon: title, text, and whether it is shown as an error.</summary>
        public static (string Title, string Body, bool IsError) Balloon(StopNotice notice, Func<string, object?[], string> format)
        {
            string name = notice.ServiceName;
            var (title, body, args, isError) = (notice.DesktopTask, notice.Kind) switch
            {
                (false, StopNoticeKind.UnexpectedStop) => ("M.Dash.UnexpectedStopTitle", "M.Dash.UnexpectedStopBody", new object?[] { name }, true),
                (false, StopNoticeKind.RepeatedStops) => ("M.Dash.StopLoopTitle", "M.Dash.StopLoopBody", new object?[] { name, notice.Count }, true),
                (false, StopNoticeKind.CleanStop) => ("M.Dash.CleanStopTitle", "M.Dash.CleanStopBody", new object?[] { name }, false),
                (false, _) => ("M.Dash.RecoveredTitle", "M.Dash.RecoveredBody", new object?[] { name }, false),
                (true, StopNoticeKind.RepeatedStops) => ("M.Task.StopLoopTitle", "M.Task.StopLoopBody", new object?[] { name, notice.Count }, true),
                (true, StopNoticeKind.Recovered) => ("M.Task.RecoveredTitle", "M.Task.RecoveredBody", new object?[] { name }, false),
                (true, _) => ("M.Task.StopTitle", "M.Task.StopBody", new object?[] { name, ExitCodeOf(notice) }, true),
            };

            return (format(title, Array.Empty<object?>()), format(body, args), isError);
        }

        /// <summary>
        /// The group chat's message, before any line saying why: machine, name, and what happened,
        /// with the exit code and the count where there are any.
        /// </summary>
        public static string Message(StopNotice notice, string machine, Func<string, object?[], string> format)
        {
            string name = notice.ServiceName;
            return (notice.DesktopTask, notice.Kind) switch
            {
                (false, StopNoticeKind.UnexpectedStop) => format("M.Alert.Stopped", new object?[] { machine, name, ExitCodeOf(notice) }),
                (false, StopNoticeKind.RepeatedStops) => format("M.Alert.StopLoop", new object?[] { machine, name, ExitCodeOf(notice), notice.Count }),
                (false, StopNoticeKind.CleanStop) => format("M.Alert.CleanStop", new object?[] { machine, name }),
                (false, _) => format("M.Alert.Recovered", new object?[] { machine, name }),
                (true, StopNoticeKind.RepeatedStops) => format("M.Alert.TaskStopLoop", new object?[] { machine, name, ExitCodeOf(notice), notice.Count }),
                (true, StopNoticeKind.Recovered) => format("M.Alert.TaskRecovered", new object?[] { machine, name }),
                (true, _) => format("M.Alert.TaskStopped", new object?[] { machine, name, ExitCodeOf(notice) }),
            };
        }

        /// <summary>What the tray balloon for <paramref name="notice"/> is tagged with, which a click on it hands back.</summary>
        public static string TagFor(StopNotice notice) =>
            notice.DesktopTask ? TaskTagPrefix + notice.ServiceName : notice.ServiceName;

        /// <summary>Whether a balloon's tag names a desktop task rather than a service, and which.</summary>
        public static bool IsTaskTag(string tag, out string taskName)
        {
            bool task = tag.StartsWith(TaskTagPrefix, StringComparison.Ordinal) && tag.Length > TaskTagPrefix.Length;
            taskName = task ? tag.Substring(TaskTagPrefix.Length) : string.Empty;
            return task;
        }

        /// <summary>
        /// The exit code as the Last stop card shows a program's: a task's result is often an
        /// NTSTATUS such as 0xC000013A, which is looked up in hex. A service's Win32 code reads the
        /// same as before.
        /// </summary>
        private static string ExitCodeOf(StopNotice notice) => LastStopReader.ExitCodeText(notice.ExitCode);
    }

    /// <summary>
    /// Decides which of a service's stops are told, and how, from the states the dashboard's poll
    /// reads: the first crash at once, the stops after it counted into one notice when the window
    /// ends, and one notice once the crashed service has kept running again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A service in a restart loop stops every few seconds for as long as the loop lasts. Told
    /// one stop at a time, that fills the tray with balloons and the group chat with messages;
    /// told once per five minutes with nothing else, which is what this replaced, a loop reads
    /// exactly like one crash. So the first crash is told at once and opens a window, the stops
    /// in it after that are counted, and when <see cref="Window"/> has passed the count goes out
    /// in one notice. A service that stopped again in that window is still looping, so a new
    /// window starts there and ends the same way: a loop that goes on for an hour is one notice
    /// at the start and one every five minutes after it, each with its count. A window that ends
    /// with nothing new in it ends the run of notices, and the next crash is told at once again.
    /// </para>
    /// <para>
    /// A stop with exit code 0 outside a window opens none. Windows' recovery does not act on
    /// it, so nothing is restarting the service in a loop, and a crash after someone has
    /// started it again is news of its own. It is told a reading late, and not at all when that
    /// reading finds the service starting or running again: a restart — the scheduled one at
    /// night runs <c>winsw restart</c> — leaves the service stopped for under a second between
    /// its stop and its start, and a reading that lands there without having seen it stopping
    /// would otherwise tell the group that a service which is back a second later has stopped.
    /// </para>
    /// <para>
    /// A window ends by the clock, not at the next stop: the count goes out at the first reading
    /// that finds the window over, and the poll makes one every couple of seconds whether or not
    /// anything has changed. A loop that stops for good halfway through a window is still
    /// counted to the end of it.
    /// </para>
    /// <para>
    /// A stop is a service last seen running and now seen stopped. One seen stopping on the way
    /// was asked to stop, by an administrator, a script or a scheduled restart, and is left
    /// alone; a program that exits, and a wrapper that dies, go from running to stopped with
    /// nothing in between. A reading that could not be made says nothing and is passed over.
    /// </para>
    /// <para>
    /// A stop the console itself is causing, on a service an operation holds (see
    /// <see cref="OperationsInFlight"/>), is noted but neither told nor counted. Noted matters:
    /// the last state still moves on, so the stop is not found again as a new one when the
    /// operation lets go, and a crashed service the console starts again still starts the clock
    /// for its recovered notice.
    /// </para>
    /// <para>
    /// Plain logic, with the time handed in. The dashboard calls it on the UI thread, once per
    /// service per reading, and raises what comes back.
    /// </para>
    /// </remarks>
    public sealed class CrashAnnouncer
    {
        /// <summary>How long the stops after a told crash are counted before the count goes out.</summary>
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

        /// <summary>How long a crashed service has to keep running before it is told to have recovered.</summary>
        public static readonly TimeSpan RecoveredAfter = TimeSpan.FromMinutes(2);

        private static readonly IReadOnlyList<StopNotice> None = Array.Empty<StopNotice>();

        private readonly Dictionary<string, Track> tracks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Takes one reading of <paramref name="serviceName"/> and returns what is to be told
        /// because of it, in the order it is to be told; usually nothing.
        /// </summary>
        /// <param name="serviceName">The service read.</param>
        /// <param name="status">Its state; null when it could not be read.</param>
        /// <param name="exitCode">The exit code the service control manager holds for its last stop.</param>
        /// <param name="held">An operation from the console is working on the service, and a stop now is that operation's.</param>
        /// <param name="now">The time of the reading, UTC.</param>
        public IReadOnlyList<StopNotice> Observe(string serviceName, ServiceControllerStatus? status, int exitCode, bool held, DateTime now)
        {
            if (!this.tracks.TryGetValue(serviceName, out var track))
            {
                track = new Track();
                this.tracks.Add(serviceName, track);
            }

            List<StopNotice>? notices = null;

            // The window first, so that a stop on the tick that ends it is the first of a new
            // run rather than one more for a count that is going out without it.
            if (track.WindowStart is { } start && now - start >= Window)
            {
                if (track.Untold > 0)
                {
                    Tell(new StopNotice(serviceName, StopNoticeKind.RepeatedStops, track.Count, track.LastExitCode));
                    track.RecoveryOwed = true;

                    // Still looping, very likely: the next window follows on without a notice
                    // at its start, and ends like this one.
                    track.WindowStart = now;
                    track.Before = track.Count;
                    track.Count = 0;
                    track.Untold = 0;
                }
                else
                {
                    track.WindowStart = null;
                    track.Before = 0;
                    track.Count = 0;
                }
            }

            if (status is { } current)
            {
                // A clean stop seen at the last reading: told now that the service is seen to have
                // stayed stopped, which overtakes a crash told before it — the service did run
                // again, and this is the latest that was said about it. Dropped as a restart if the
                // service is on its way back, which leaves a recovery owed to be told as usual.
                if (track.CleanStopHeld)
                {
                    track.CleanStopHeld = false;
                    if (current is not (ServiceControllerStatus.StartPending or ServiceControllerStatus.Running))
                    {
                        Tell(new StopNotice(serviceName, StopNoticeKind.CleanStop, 1, 0));
                        track.RecoveryOwed = false;
                    }
                }

                bool stopped = track.Last == ServiceControllerStatus.Running && current == ServiceControllerStatus.Stopped;

                if (current != ServiceControllerStatus.Running)
                {
                    track.RunningSince = null;
                }
                else if (track.Last != ServiceControllerStatus.Running)
                {
                    track.RunningSince = now;
                }

                track.Last = current;

                if (stopped && !held)
                {
                    track.LastExitCode = exitCode;
                    if (track.WindowStart != null)
                    {
                        // Part of a loop already told; whatever its exit code, one more for the count.
                        track.Count++;
                        track.Untold++;
                    }
                    else if (exitCode == 0)
                    {
                        // Not a crash: nothing to count on from here, and nothing to recover
                        // from. Held for the next reading, which tells it or drops it; see above.
                        track.CleanStopHeld = true;
                    }
                    else
                    {
                        track.WindowStart = now;
                        track.Before = 0;
                        track.Count = 1;
                        track.Untold = 0;
                        track.RecoveryOwed = true;
                        Tell(new StopNotice(serviceName, StopNoticeKind.UnexpectedStop, 1, exitCode));
                    }
                }
            }

            if (track.RecoveryOwed && track.RunningSince is { } since && now - since >= RecoveredAfter)
            {
                // Stops not yet told go out first: "running again" followed by a count that
                // says it keeps being restarted would read as the opposite of what happened.
                if (track.Untold > 0)
                {
                    Tell(new StopNotice(serviceName, StopNoticeKind.RepeatedStops, track.Count, track.LastExitCode));
                    track.Untold = 0;
                }

                Tell(new StopNotice(serviceName, StopNoticeKind.Recovered, 0, track.LastExitCode));
                track.RecoveryOwed = false;
            }

            return notices ?? None;

            void Tell(StopNotice notice) => (notices ??= new List<StopNotice>()).Add(notice);
        }

        /// <summary>
        /// The stops of <paramref name="serviceName"/> in the window now open, the one told at
        /// once included; 0 when there is none.
        /// </summary>
        public int CountFor(string serviceName) =>
            this.tracks.TryGetValue(serviceName, out var track) && track.WindowStart != null ? track.Count : 0;

        /// <summary>
        /// The stops of <paramref name="serviceName"/> lately: those in the window now open and,
        /// when it follows on from a window whose count went out, those of that window too; 0 when
        /// no window is open. What the dashboard flags a service as needing attention for.
        /// </summary>
        /// <remarks>
        /// Not <see cref="CountFor"/>, which starts again from 0 in a window that follows on: a
        /// loop between two of its stops, running for the moment, would drop out of "needs
        /// attention" at every window's end until it next stopped. A service stops being flagged
        /// only when a whole window has passed without a stop.
        /// </remarks>
        public int RecentStopsFor(string serviceName) =>
            this.tracks.TryGetValue(serviceName, out var track) && track.WindowStart != null ? track.Before + track.Count : 0;

        /// <summary>
        /// Drops everything noted about <paramref name="serviceName"/>: it is gone from the list,
        /// or what is installed under the name is no longer what was noted. A count still to go
        /// out goes with it.
        /// </summary>
        public void Forget(string serviceName) => this.tracks.Remove(serviceName);

        private sealed class Track
        {
            /// <summary>The last state read; null until one has been.</summary>
            public ServiceControllerStatus? Last { get; set; }

            /// <summary>When the service was last seen to start running; null while it is not.</summary>
            public DateTime? RunningSince { get; set; }

            /// <summary>When the open window began; null when none is open.</summary>
            public DateTime? WindowStart { get; set; }

            /// <summary>Stops in the open window, told or not.</summary>
            public int Count { get; set; }

            /// <summary>
            /// Stops in the window the open one follows on from, whose count has gone out; 0 when
            /// the open window is the first of its run, or none is open.
            /// </summary>
            public int Before { get; set; }

            /// <summary>Stops in the open window that no notice has covered yet.</summary>
            public int Untold { get; set; }

            public int LastExitCode { get; set; }

            /// <summary>A crash was told, and the notice that it is running again has not been.</summary>
            public bool RecoveryOwed { get; set; }

            /// <summary>A stop with exit code 0 was seen at the last reading and is not told yet: it may be a restart.</summary>
            public bool CleanStopHeld { get; set; }
        }
    }
}
