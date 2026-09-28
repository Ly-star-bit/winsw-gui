using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Another console running in this logon session, found by looking at its process rather
    /// than by asking it: the way to reach a copy from before <see cref="ConsoleLaunch"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every release up to this one answers a second launch only by bringing its window
    /// forward. It cannot be told who the launch is, so it cannot offer to hand over; a newer
    /// executable double-clicked beside it just woke it. The launch now finds it here, asks the
    /// user, and ends it: nothing else makes such a copy let go of the session. Ending it loses
    /// whatever it had not saved, which the question says, and leaves its tray icon behind until
    /// the pointer passes over it, which is how Windows tidies an icon whose owner is gone.
    /// </para>
    /// <para>
    /// A console is recognised by its executable's version resource, not by its name: the
    /// release assets are named after their build, and people keep a download's name or give it
    /// one of their own. So every process in the session is looked at, each executable's
    /// resource read once however many processes it runs as — a few dozen files, on a path taken
    /// only when the running copy did not answer. The wrapper is told apart by the same resource.
    /// </para>
    /// </remarks>
    public sealed class RunningCopy
    {
        /// <summary>The name every console build's version resource gives as its original file.</summary>
        internal const string ConsoleOriginalFilename = "WinSW.Gui.dll";

        /// <summary>How long ending a copy waits for its process to be gone.</summary>
        private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(5);

        internal RunningCopy(int processId, DateTime startTime, string executablePath, string version)
        {
            this.ProcessId = processId;
            this.StartTime = startTime;
            this.ExecutablePath = executablePath;
            this.Version = version;
        }

        public int ProcessId { get; }

        /// <summary>Tells this process from a later one that was given the same id.</summary>
        public DateTime StartTime { get; }

        public string ExecutablePath { get; }

        /// <summary>The version its executable declares, as <see cref="UpdateChecker.CurrentGuiVersion"/> gives this one's.</summary>
        public string Version { get; }

        /// <summary>
        /// The other consoles in this session that are not this one's executable at this one's
        /// version, and that this launch could end. A process whose executable cannot be read —
        /// a protected one, or one gone since the list was taken — is left out, and so is one
        /// this launch has no rights over: a console running as administrator, when the launch
        /// is not. Offered, it would only end in a refusal.
        /// </summary>
        public static IReadOnlyList<RunningCopy> FindOthers(string ownVersion, string ownExecutablePath)
        {
            var found = new List<RunningCopy>();

            // Executable → the console version it declares, or null for anything else.
            var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            using var self = Process.GetCurrentProcess();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == self.Id || process.SessionId != self.SessionId)
                        {
                            continue;
                        }

                        string? path = NativeMethods.ImagePathOf(process.Id);
                        if (path is null)
                        {
                            continue;
                        }

                        if (!versions.TryGetValue(path, out string? version))
                        {
                            var info = FileVersionInfo.GetVersionInfo(path);
                            version = IsConsole(info.OriginalFilename) ? VersionOf(info.ProductVersion) : null;
                            versions[path] = version;
                        }

                        if (version is null || new ConsoleLaunch(version, path, null).IsSameConsole(ownVersion, ownExecutablePath))
                        {
                            continue;
                        }

                        // Opens the process with every right, as ending it will; throws for a
                        // process this account may look at but not touch.
                        _ = process.Handle;
                        found.Add(new RunningCopy(process.Id, process.StartTime, path, version));
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
                    {
                        // Gone since the list was taken, or closed to this process.
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Ends each copy and waits for it to be gone. False when one could not be ended, which
        /// is <paramref name="failed"/>; the ones before it have been.
        /// </summary>
        /// <param name="reason">What Windows said; null when the copy was told to end and did not within the time allowed.</param>
        public static bool TryEnd(IEnumerable<RunningCopy> copies, out RunningCopy? failed, out string? reason)
        {
            failed = null;
            reason = null;
            foreach (var copy in copies)
            {
                try
                {
                    using var process = Process.GetProcessById(copy.ProcessId);

                    // Not the one that was asked about: that one has ended, and its id is now
                    // someone else's.
                    if (process.StartTime != copy.StartTime)
                    {
                        continue;
                    }

                    process.Kill();
                    if (process.WaitForExit((int)EndTimeout.TotalMilliseconds))
                    {
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    // Already gone.
                    continue;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    reason = e.Message;
                }

                failed = copy;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a version resource is a console's. The wrapper's says "WinSW.dll" or
        /// "WinSW.exe"; every console build, the self-contained ones included, carries the
        /// managed assembly's "WinSW.Gui.dll".
        /// </summary>
        internal static bool IsConsole(string? originalFilename) =>
            string.Equals(originalFilename, ConsoleOriginalFilename, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// A product version as <see cref="UpdateChecker.CurrentGuiVersion"/> writes one: without
        /// a source-link suffix such as "+abc123". Blank for a file that declares none.
        /// </summary>
        internal static string VersionOf(string? productVersion)
        {
            string version = productVersion?.Trim() ?? string.Empty;
            int plus = version.IndexOf('+');
            return plus > 0 ? version.Substring(0, plus) : version;
        }
    }
}
