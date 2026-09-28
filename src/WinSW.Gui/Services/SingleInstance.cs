using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// One console per logon session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starting with Windows puts a console in the tray at every sign-in, where it keeps
    /// polling so that a service that stops still raises a notification. Launched again from
    /// a shortcut, a second copy used to start beside it: two windows' worth of polling, and
    /// every unexpected stop announced twice. A second launch now brings the first copy's
    /// window forward and exits.
    /// </para>
    /// <para>
    /// A launch with a configuration to open — the Explorer verb, or a path on the command
    /// line — hands the path to the running copy, which opens it; see <see cref="ConfigHandoff"/>.
    /// It used to start a copy of its own, and that one announced every stop a second time.
    /// </para>
    /// <para>
    /// Two launches still go ahead on their own. A copy running elevated cannot be reached from
    /// a standard one: the objects it created are closed to a lower integrity level, so the
    /// check fails open rather than refusing to start. And a launch with a configuration whose
    /// running copy does not take it — hung, or a version that does not listen — opens the file
    /// itself rather than losing it.
    /// </para>
    /// </remarks>
    public sealed class SingleInstance : IDisposable
    {
        /// <summary>Held by the running copy. <c>Local\</c>: one per logon session, not per machine.</summary>
        private const string MutexName = @"Local\WinSW.Gui.Instance";

        /// <summary>Set by a second launch to ask the running copy to show its window.</summary>
        private const string ShowEventName = @"Local\WinSW.Gui.Show";

        /// <summary>
        /// How long a copy started by "Restart as administrator" waits for the one that started
        /// it to finish closing. That copy shuts down straight after the launch, so this is a
        /// ceiling, not a delay.
        /// </summary>
        private static readonly TimeSpan ReplaceTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long letting go waits for the pipe to close. It closes as soon as it is told to;
        /// this only keeps a stuck one from holding up the exit.
        /// </summary>
        private static readonly TimeSpan HandoffCloseTimeout = TimeSpan.FromSeconds(2);

        private readonly Mutex? mutex;
        private readonly EventWaitHandle? showRequests;
        private readonly CancellationTokenSource handoffStop = new();
        private RegisteredWaitHandle? registration;
        private Task? handoff;

        private SingleInstance(Mutex? mutex, EventWaitHandle? showRequests)
        {
            this.mutex = mutex;
            this.showRequests = showRequests;
        }

        /// <summary>
        /// Claims the session for this process. Null means another copy already holds it, and
        /// has been given <paramref name="configPath"/> to open or — when
        /// <paramref name="wake"/> is set — asked to bring its window forward; this one should
        /// exit.
        /// </summary>
        /// <param name="replacing">
        /// This copy was started by the one it is replacing, which is on its way out: wait for it
        /// to go rather than deferring to it.
        /// </param>
        /// <param name="wake">
        /// Ask the running copy to show its window. Not for a launch at sign-in, which is meant
        /// to stay in the tray.
        /// </param>
        /// <param name="configPath">
        /// A configuration this launch was asked to open, as a full path. The running copy is
        /// handed it, and shows its window to open it. When that copy does not take it, the
        /// claim still succeeds, without the session: this launch opens the file as a copy of its
        /// own, and the running one stays the one later launches reach.
        /// </param>
        public static SingleInstance? Claim(bool replacing, bool wake, string? configPath = null)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(false, MutexName);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
            {
                // Held by an elevated copy, which this one can neither wait on nor wake.
                return new SingleInstance(null, null);
            }

            bool acquired;
            try
            {
                acquired = mutex.WaitOne(replacing ? ReplaceTimeout : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner exited without letting go. It is gone, which is all that
                // mattered; the mutex is now this process's.
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                if (configPath != null)
                {
                    return HandOver(configPath) ? null : new SingleInstance(null, null);
                }

                if (wake)
                {
                    WakeRunningCopy();
                }

                return null;
            }

            EventWaitHandle? showRequests = null;
            try
            {
                showRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
            {
                // A second launch will not be able to wake this copy, and will start its own.
            }

            return new SingleInstance(mutex, showRequests);
        }

        /// <summary>
        /// Calls <paramref name="show"/> — on a thread-pool thread — each time a second launch
        /// asks for this copy's window.
        /// </summary>
        public void OnShowRequested(Action show)
        {
            if (this.showRequests is null || this.registration != null)
            {
                return;
            }

            this.registration = ThreadPool.RegisterWaitForSingleObject(
                this.showRequests, (_, _) => show(), null, Timeout.Infinite, executeOnlyOnce: false);
        }

        /// <summary>
        /// Calls <paramref name="open"/> — on a thread-pool thread — with each configuration a
        /// later launch hands to this copy. Only a copy that holds the session listens.
        /// </summary>
        public void OnOpenRequested(Action<string> open)
        {
            if (this.mutex is null || this.handoff != null)
            {
                return;
            }

            this.handoff = ConfigHandoff.Listen(open, e => ErrorLog.Record("configuration hand-off", e), this.handoffStop.Token);
        }

        /// <summary>
        /// Lets go of the session. Called on the thread that claimed it, which is the only one
        /// that may release the mutex; were it not, the mutex would be abandoned at exit, which
        /// the next claim treats the same way.
        /// </summary>
        /// <remarks>
        /// The pipe is closed before the mutex is released. A copy started by "Restart as
        /// administrator" takes the session the moment the mutex is let go, and opens a pipe of
        /// the same name straight after.
        /// </remarks>
        public void Dispose()
        {
            this.registration?.Unregister(null);
            this.showRequests?.Dispose();

            this.handoffStop.Cancel();
            try
            {
                this.handoff?.Wait(HandoffCloseTimeout);
            }
            catch (AggregateException)
            {
                // It ended by failing, which it has already recorded; it has ended.
            }

            this.handoffStop.Dispose();

            if (this.mutex != null)
            {
                try
                {
                    this.mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not owned by this thread after all.
                }

                this.mutex.Dispose();
            }
        }

        private static bool HandOver(string configPath)
        {
            // As when waking it: this process is the one the user just started, so it is the one
            // allowed to hand the foreground on to the window that opens the file.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            return ConfigHandoff.TrySend(configPath);
        }

        private static void WakeRunningCopy()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var running))
                {
                    using (running)
                    {
                        // This process is the one the user just started, so it is the one allowed
                        // to hand the foreground on.
                        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
                        running.Set();
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // The running copy cannot be reached. It is still running; this one exits anyway.
            }
        }
    }
}
