using System;
using System.Text;
using System.Threading;

namespace WinSW
{
    /// <summary>
    /// The rendezvous between a wrapper running in console mode and whatever wants it to stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A service is stopped through the service control manager. A wrapper started by the task
    /// scheduler in a logged-on session has no such channel: the only thing outside it knows is
    /// the configuration it was started with. So it publishes a named event derived from the
    /// service ID, waits on it, and shuts the child down cleanly when it is signalled.
    /// </para>
    /// <para>
    /// The name is scoped to the session (<c>Local\</c>), which is where both halves live: the
    /// task runs in the interactive session, and so does anything with a window that wants to
    /// stop it. <strong>The console front end reimplements <see cref="EventName"/> verbatim</strong>
    /// -- it does not reference this assembly -- so the two must be changed together.
    /// </para>
    /// </remarks>
    public static class ConsoleSession
    {
        /// <summary>
        /// The event a console-mode wrapper for <paramref name="serviceId"/> waits on.
        /// </summary>
        /// <remarks>
        /// A kernel object name may not contain a backslash beyond the namespace prefix, so
        /// anything outside a conservative set is replaced. Because that mapping is not
        /// injective, a hash of the original ID is appended to keep two services that differ
        /// only in punctuation apart.
        /// </remarks>
        public static string EventName(string serviceId)
        {
            var builder = new StringBuilder(@"Local\WinSW.Console.", 64);

            int length = Math.Min(serviceId.Length, 100);
            for (int i = 0; i < length; i++)
            {
                char c = serviceId[i];
                builder.Append((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_' ? c : '_');
            }

            return builder.Append('.').Append(Hash(serviceId).ToString("x8")).ToString();
        }

        /// <summary>
        /// Signals a console-mode wrapper for <paramref name="serviceId"/> to stop.
        /// </summary>
        /// <returns>
        /// False when no such wrapper is running in this session, or when it runs at an
        /// integrity level this process is not allowed to signal.
        /// </returns>
        public static bool RequestStop(string serviceId)
        {
            try
            {
                if (!EventWaitHandle.TryOpenExisting(EventName(serviceId), out var handle))
                {
                    return false;
                }

                using (handle)
                {
                    return handle.Set();
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.IO.IOException)
            {
                return false;
            }
        }

        /// <summary>FNV-1a, for a stable short suffix. Nothing here depends on it being strong.</summary>
        private static uint Hash(string value)
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }
}
