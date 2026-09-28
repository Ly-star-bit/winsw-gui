using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WinSW.Gui.Services
{
    public static class Elevation
    {
        public static bool IsElevated { get; } = Detect();

        /// <summary>
        /// Starts a copy of the GUI with administrator rights, to take over from this one. Returns
        /// false when the UAC prompt was declined, in which case nothing has changed. On true the
        /// caller closes this copy, which the new one is waiting for.
        /// </summary>
        /// <param name="configPath">The configuration the new copy is to open, if any.</param>
        /// <param name="keepTray">
        /// This copy watches from the tray; see <see cref="StartupArguments.KeepTrayArgument"/>.
        /// </param>
        /// <remarks>
        /// It used to shut the application down itself, and the window took that for an ordinary
        /// close: one kept to the tray, or with unsaved changes, cancelled it — which a shutdown
        /// ignores — and so went without saving its placement, or put the unsaved-changes prompt
        /// up in a window on its way out. The window now closes as an exit already answered for.
        /// </remarks>
        public static bool RestartElevated(string? configPath, bool keepTray)
        {
            string? path = Environment.ProcessPath;
            if (path is null)
            {
                return false;
            }

            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            };

            // One element per argument: the runtime quotes each one for the command line, so a
            // configuration in "C:\Program Files" arrives whole. It does so for a shell launch too.
            foreach (string argument in StartupArguments.ForElevatedRestart(configPath, keepTray))
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                Process.Start(start);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return false;
            }

            return true;
        }

        private static bool Detect()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
