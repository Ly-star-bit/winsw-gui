using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Decides which of a desktop task's stops are told, and how: the rule a service's follow,
    /// <see cref="CrashAnnouncer"/>, fed with the task scheduler's state and last result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A robot that crashes every few minutes is started again by its keep-alive trigger within
    /// the minute, and nobody heard of it: its last result was on the task page alone, which reads
    /// only while it is open. The desktop-task page now reads its folder in the background as
    /// well, and every reading comes here. What is told comes out as a <see cref="StopNotice"/>
    /// marked <see cref="StopNotice.DesktopTask"/>, for the same balloon and group chat as a
    /// service's, held to the same one count per five minutes.
    /// </para>
    /// <para>
    /// A task seen running and then ready again has ended; it crashed when its last result says it
    /// failed and the console did not end it. Its program ending with 0 is not told: that is a
    /// robot finishing its work, or closed by the person at the desktop, and the keep-alive
    /// trigger starting it again is what it is for. Nor is a task ended from the task scheduler
    /// (<see cref="Terminated"/>), or one that was disabled, which somebody meant.
    /// </para>
    /// <para>
    /// A known limit, the same a service's has at a shorter interval: a task that ends and is
    /// started again between two readings is never seen stopped. The keep-alive starts it up to
    /// a minute after it ends, and behind the page the folder is read every half-minute, so a
    /// crash repaired at once can go untold. Its last result is lost by then too: a running
    /// task's is <see cref="StillRunning"/>.
    /// </para>
    /// <para>Plain logic, with the time handed in. The page calls it on the UI thread, once per task per reading.</para>
    /// </remarks>
    public sealed class DesktopTaskWatch
    {
        /// <summary>SCHED_S_TASK_RUNNING: the last result a task holds while an instance of it runs.</summary>
        internal const int StillRunning = 0x41301;

        /// <summary>SCHED_S_TASK_HAS_NOT_RUN: the last result of a task that has never run.</summary>
        internal const int NeverRun = 0x41303;

        /// <summary>SCHED_S_TASK_TERMINATED: the last run was ended by a user, from the task scheduler.</summary>
        internal const int Terminated = 0x41306;

        private readonly CrashAnnouncer announcer = new();

        /// <summary>
        /// Takes one reading of the task <paramref name="name"/> and returns what is to be told
        /// because of it, in order; usually nothing.
        /// </summary>
        /// <param name="name">The task's name.</param>
        /// <param name="state">Its state.</param>
        /// <param name="lastResult">Its last run's result, which is its program's exit code once the run is over.</param>
        /// <param name="requested">The console is stopping, restarting, disabling or deleting it, and a stop now is that.</param>
        /// <param name="now">The time of the reading, UTC.</param>
        public IReadOnlyList<StopNotice> Observe(string name, DesktopTaskState state, int lastResult, bool requested, DateTime now)
        {
            var (status, quiet) = Reading(state, lastResult);
            var notices = this.announcer.Observe(name, status, lastResult, requested || quiet, now);
            return notices.Count == 0 ? notices : notices.Select(notice => notice with { DesktopTask = true }).ToList();
        }

        /// <summary>Drops everything noted about <paramref name="name"/>: the task is gone from the folder.</summary>
        public void Forget(string name) => this.announcer.Forget(name);

        /// <summary>
        /// A task's state as the state of a service, which is what the rule reads, and whether a
        /// stop in it is one to leave untold. Null for a reading that says nothing, which is passed
        /// over: an unknown state, and a task ready again whose result still says it is running,
        /// which the next reading will have caught up with.
        /// </summary>
        internal static (ServiceControllerStatus? Status, bool Quiet) Reading(DesktopTaskState state, int lastResult) => state switch
        {
            DesktopTaskState.Running => (ServiceControllerStatus.Running, false),
            DesktopTaskState.Queued => (ServiceControllerStatus.StartPending, false),
            DesktopTaskState.Ready when lastResult == StillRunning => (null, false),
            DesktopTaskState.Ready => (ServiceControllerStatus.Stopped, lastResult is 0 or NeverRun or Terminated),
            DesktopTaskState.Disabled => (ServiceControllerStatus.Stopped, true),
            _ => (null, false),
        };
    }
}
