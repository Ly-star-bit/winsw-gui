using System;
using Microsoft.Win32;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The "Open in WinSW" entry on the context menu of .xml files. Registered per user
    /// under HKCU, so it needs no elevation and uninstalls with a single key deletion.
    /// </summary>
    public static class ShellIntegration
    {
        private const string VerbKey = @"Software\Classes\SystemFileAssociations\.xml\shell\WinSW.Gui";

        public static bool IsRegistered
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(VerbKey + @"\command");
                return key?.GetValue(null) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Register(string label)
        {
            using var key = Registry.CurrentUser.CreateSubKey(VerbKey);
            key.SetValue(null, label);
            key.SetValue("Icon", $"\"{ExecutablePath}\",0");

            using var command = key.CreateSubKey("command");
            command.SetValue(null, $"\"{ExecutablePath}\" \"%1\"");
        }

        public static void Unregister()
        {
            Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);
        }

        private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;
    }
}
