using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace WinSW.Gui.Services
{
    public static class Elevation
    {
        public static bool IsElevated { get; } = Detect();

        /// <summary>
        /// Relaunches the GUI with administrator rights and closes this instance. Returns false
        /// when the UAC prompt was declined, in which case nothing has changed.
        /// </summary>
        public static bool RestartElevated()
        {
            string? path = Environment.ProcessPath;
            if (path is null)
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Environment.CurrentDirectory,
                });
            }
            catch (Win32Exception e) when (e.NativeErrorCode == NativeMethods.ERROR_CANCELLED)
            {
                return false;
            }

            Application.Current.Shutdown();
            return true;
        }

        private static bool Detect()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
