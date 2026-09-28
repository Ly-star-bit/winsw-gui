using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A console that is to take over from this one: the executable a later launch came from,
    /// or this one's own, once "Update now" has put the new release in its place.
    /// </summary>
    /// <remarks>
    /// It takes over the way the copy started by "Restart as administrator" does. It is started
    /// with <see cref="StartupArguments.ReplaceArgument"/>, and so waits for this one to let go of
    /// the session rather than deferring to it; it keeps the tray watch this one keeps, and opens
    /// the configuration this one had on screen. Unsaved changes are asked about before any of it
    /// happens, as before a restart.
    /// </remarks>
    public sealed class Replacement
    {
        /// <param name="executablePath">The console that takes over.</param>
        /// <param name="version">Its version, for the prompt that stands before it.</param>
        /// <param name="configPath">A configuration it is to open in place of the one this copy has on screen: the one its launch came with.</param>
        /// <param name="downloadedFile">
        /// For an update, the checked release beside <paramref name="executablePath"/>, which is
        /// put in its place only now, once there is nothing left to ask; null when the executable
        /// is already there.
        /// </param>
        public Replacement(string executablePath, string version, string? configPath, string? downloadedFile)
        {
            this.ExecutablePath = executablePath;
            this.Version = version;
            this.ConfigPath = configPath;
            this.DownloadedFile = downloadedFile;
        }

        public string ExecutablePath { get; }

        public string Version { get; }

        public string? ConfigPath { get; }

        public string? DownloadedFile { get; }

        /// <summary>This is "Update now" restarting into the release it fetched.</summary>
        public bool IsUpdate => this.DownloadedFile != null;

        /// <summary>
        /// Puts an update's file in place, then starts the console that takes over. Null when it
        /// is on its way, and this copy is to close at once; otherwise what went wrong, with
        /// nothing changed: a file that was put in place has been taken back out.
        /// </summary>
        /// <param name="configPath">The configuration the new console is to open, if any.</param>
        /// <param name="keepTray">This copy watches from the tray; the new one is to do the same.</param>
        public string? Start(string? configPath, bool keepTray)
        {
            if (this.DownloadedFile != null)
            {
                try
                {
                    SelfUpdate.Swap(this.ExecutablePath, this.DownloadedFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return e.Message;
                }
            }

            // Not through the shell, and with no verb: the new console runs with this one's
            // rights, as administrator when this one is, without a prompt. The shell would also
            // hold a downloaded file up for its origin, which the release's checksum has already
            // answered for.
            var start = new ProcessStartInfo(this.ExecutablePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            foreach (string argument in StartupArguments.ForReplacement(configPath, keepTray))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var started = Process.Start(start);
                return null;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                if (this.DownloadedFile != null)
                {
                    try
                    {
                        SelfUpdate.Undo(this.ExecutablePath, this.DownloadedFile);
                    }
                    catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                    {
                        // The new executable stays where it is, and starts next time. This copy
                        // goes on running from the old one's renamed file.
                        ErrorLog.Record("update undo", undo);
                    }
                }

                return e.Message;
            }
        }

        /// <summary>
        /// Moves the sign-in entry and the Explorer verb from <paramref name="previousPath"/> to
        /// <paramref name="executablePath"/>, each only when it starts the previous one: the
        /// console there has been replaced by the one here. They used to go on starting the old
        /// executable, which is what the user had just replaced.
        /// </summary>
        /// <param name="verbLabel">The Explorer verb's text, in the language it is written in.</param>
        public static void Repoint(string previousPath, string executablePath, string verbLabel)
        {
            if (ConsoleLaunch.IsSamePath(previousPath, executablePath))
            {
                return;
            }

            try
            {
                Autostart.Repoint(previousPath, executablePath);
                ShellIntegration.Repoint(previousPath, executablePath, verbLabel);
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // HKCU is normally writable. If not, the settings page shows each as off in the
                // new console, and turning it on there repairs it.
                ErrorLog.Record("repoint sign-in entry", e);
            }
        }
    }
}
