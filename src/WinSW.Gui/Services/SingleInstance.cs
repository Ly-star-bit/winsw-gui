using System;
using System.Collections.Generic;
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
    /// <para>
    /// "Restart as administrator" starts the elevated copy while the standard one is still
    /// closing. Told so by <see cref="StartupArguments.ReplaceArgument"/>, it waits for the
    /// session rather than deferring to the copy on its way out; should that copy not be gone in
    /// time, the configuration the new copy came with is opened there, not handed to the old one.
    /// </para>
    /// <para>
    /// A second launch that is not the running console — another executable, or another
    /// version — used to wake it all the same, so that a newer download double-clicked beside
    /// an old copy in the tray brought the old one forward and looked like an update that had
    /// worked. A launch now tells the running copy who it is, and that copy offers to hand over
    /// to it; see <see cref="ConsoleLaunch"/>. A copy from before that cannot be told, and is
    /// found by its process instead: the launch offers to end it and take the session itself;
    /// see <see cref="RunningCopy"/>.
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
        /// has been told about this launch — which it answers by opening
        /// <paramref name="configPath"/>, bringing its window forward when
        /// <paramref name="wake"/> is set, or offering to hand over to this console — or, being
        /// older, asked to bring its window forward; this one should exit.
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
        /// <param name="replaceOlder">
        /// Asked, with what was found, when the running copy could not be told about this launch
        /// and is another executable or another version: a console from before a launch could say
        /// who it is. True once those consoles have been ended, and this launch is to take the
        /// session in their place; false to defer to the running copy as before. Not asked at
        /// sign-in, nor by a copy that is itself replacing another.
        /// </param>
        public static SingleInstance? Claim(bool replacing, bool wake, string? configPath = null, Func<IReadOnlyList<RunningCopy>, bool>? replaceOlder = null)
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
                return replacing ? DeferToClosingCopy(wake, configPath) : Meet(wake, configPath, replaceOlder);
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
        /// This copy is the one later launches reach. A copy that started beside another — one
        /// running as administrator, or one that did not take a configuration — is not.
        /// </summary>
        public bool HoldsSession => this.mutex != null;

        /// <summary>
        /// Calls <paramref name="open"/> — on a thread-pool thread — with each bare configuration
        /// path a later launch hands to this copy, and <paramref name="launched"/> with each launch
        /// that says who it is; see <see cref="ConsoleLaunch"/>. Only a copy that holds the session
        /// listens.
        /// </summary>
        public void OnOpenRequested(Action<string> open, Action<ConsoleLaunch> launched)
        {
            if (this.mutex is null || this.handoff != null)
            {
                return;
            }

            this.handoff = ConfigHandoff.Listen(open, launched, e => ErrorLog.Record("configuration hand-off", e), this.handoffStop.Token);
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

        /// <summary>
        /// The session is held by the copy this one is replacing, which has not closed in time.
        /// </summary>
        private static SingleInstance? DeferToClosingCopy(bool wake, string? configPath)
        {
            // Not handed to it: it is closing, and would take the file with it.
            if (configPath != null)
            {
                return new SingleInstance(null, null);
            }

            return wake && !WakeRunningCopy() ? new SingleInstance(null, null) : null;
        }

        /// <summary>
        /// The session is held by another copy, which this launch tells about itself; see
        /// <see cref="ConsoleLaunch"/>. A copy that hears it decides what happens next — it comes
        /// forward, opens the configuration, or offers to hand over — and this launch exits.
        /// </summary>
        private static SingleInstance? Meet(bool wake, string? configPath, Func<IReadOnlyList<RunningCopy>, bool>? replaceOlder)
        {
            // At sign-in, with a console already watching: nothing to show, and nobody to ask.
            if (!wake && configPath is null)
            {
                return null;
            }

            string? executable = Environment.ProcessPath;
            bool heard = executable != null
                ? HandOver(new ConsoleLaunch(UpdateChecker.CurrentGuiVersion, executable, configPath))
                : configPath != null && HandOver(configPath);
            if (heard)
            {
                return null;
            }

            // Not heard: a copy from before a launch could say who it is, a hung one, or one
            // running as administrator, which a standard launch can neither reach nor end. The
            // first is found by its process and, when it is not this console, offered to be
            // replaced; ended, it lets go of the session as its process goes, and the claim waits
            // for that as a copy started to replace another does.
            if (wake && replaceOlder != null && executable != null && IsWithinReach())
            {
                var others = RunningCopy.FindOthers(UpdateChecker.CurrentGuiVersion, executable);
                if (others.Count > 0 && replaceOlder(others))
                {
                    return Claim(replacing: true, wake, configPath);
                }
            }

            // As before a launch said who it was: a configuration is opened here, in a console
            // of its own, and anything else brings the running copy forward.
            if (configPath != null)
            {
                return new SingleInstance(null, null);
            }

            return WakeRunningCopy() ? null : new SingleInstance(null, null);
        }

        private static bool HandOver(string configPath)
        {
            // As when waking it: this process is the one the user just started, so it is the one
            // allowed to hand the foreground on to the window that opens the file.
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            return ConfigHandoff.TrySend(configPath);
        }

        private static bool HandOver(ConsoleLaunch launch)
        {
            NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);
            return ConfigHandoff.TrySend(launch);
        }

        /// <summary>
        /// Whether the copy holding the session runs with no more rights than this launch, so
        /// that it can be ended from here. Its event is closed to a launch with fewer, as in
        /// <see cref="WakeRunningCopy"/>.
        /// </summary>
        private static bool IsWithinReach()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var running))
                {
                    running.Dispose();
                }

                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }

        /// <summary>
        /// Asks the running copy to show its window. False when that copy runs as administrator
        /// and this launch does not, so that it cannot be asked; the launch then starts on its
        /// own, as it does when the session's mutex is closed to it.
        /// </summary>
        /// <remarks>
        /// The mutex alone does not tell. A copy restarted as administrator waited on the mutex
        /// the standard copy before it had created, and holds that one now, open to a standard
        /// launch; only the event it created itself is closed to one. Such a launch used to find
        /// the session taken, fail to wake the copy holding it, and exit with nothing on screen.
        /// </remarks>
        private static bool WakeRunningCopy()
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
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                // The running copy cannot be reached. It is still running; this one exits anyway.
            }

            return true;
        }
    }
}
