using System;
using System.IO;
using System.Threading;

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
    /// Two launches are let through as before. One with a configuration to open — the
    /// Explorer verb, or a path on the command line — has something to show that the running
    /// copy was not told about. And a copy running elevated cannot be reached from a standard
    /// one: the objects it created are closed to a lower integrity level, so the check fails
    /// open rather than refusing to start.
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

        private readonly Mutex? mutex;
        private readonly EventWaitHandle? showRequests;
        private RegisteredWaitHandle? registration;

        private SingleInstance(Mutex? mutex, EventWaitHandle? showRequests)
        {
            this.mutex = mutex;
            this.showRequests = showRequests;
        }

        /// <summary>
        /// Claims the session for this process. Null means another copy already holds it, and
        /// — when <paramref name="wake"/> is set — has been asked to bring its window forward;
        /// this one should exit.
        /// </summary>
        /// <param name="replacing">
        /// This copy was started by the one it is replacing, which is on its way out: wait for it
        /// to go rather than deferring to it.
        /// </param>
        /// <param name="wake">
        /// Ask the running copy to show its window. Not for a launch at sign-in, which is meant
        /// to stay in the tray.
        /// </param>
        public static SingleInstance? Claim(bool replacing, bool wake)
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
        /// Lets go of the session. Called on the thread that claimed it, which is the only one
        /// that may release the mutex; were it not, the mutex would be abandoned at exit, which
        /// the next claim treats the same way.
        /// </summary>
        public void Dispose()
        {
            this.registration?.Unregister(null);
            this.showRequests?.Dispose();

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
