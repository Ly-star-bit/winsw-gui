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
        /// between two readings, so it is told at once as a stop, not as a crash, and without
        /// a cause.
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
    public sealed record StopNotice(string ServiceName, StopNoticeKind Kind, int Count, int ExitCode);

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
    /// A stop with exit code 0 outside a window is told at once and opens none. Windows'
    /// recovery does not act on it, so nothing is restarting the service in a loop, and a crash
    /// after someone has started it again is news of its own.
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
                        // from. A crash told before it has been overtaken by it: the service
                        // did run again, and this is the latest that was said about it.
                        Tell(new StopNotice(serviceName, StopNoticeKind.CleanStop, 1, 0));
                        track.RecoveryOwed = false;
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
        }
    }
}
