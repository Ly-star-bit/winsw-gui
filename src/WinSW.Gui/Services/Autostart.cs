using System;
using Microsoft.Win32;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Starts the console at sign-in, in the tray. Registered per user under HKCU, like the
    /// Explorer verb, so it needs no elevation and is removed with a single value.
    /// </summary>
    /// <remarks>
    /// The notification for a service that stops unexpectedly comes from the console's own
    /// polling, so it only ever reached someone who had remembered to open the console since
    /// signing in. Starting it with Windows is what makes the tray a watch rather than a
    /// place to put a window.
    /// </remarks>
    public static class Autostart
    {
        /// <summary>The argument that starts the console hidden, with only the tray icon showing.</summary>
        public const string TrayArgument = "--tray";

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string ValueName = "WinSW.Gui";

        /// <summary>
        /// True when sign-in starts this copy of the console. An entry left by a copy that has
        /// since moved does not count, so the setting reads as off and turning it on repairs it.
        /// </summary>
        public static bool IsRegistered
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Register()
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{ExecutablePath}\" {TrayArgument}");
        }

        public static void Unregister()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;
    }
}
