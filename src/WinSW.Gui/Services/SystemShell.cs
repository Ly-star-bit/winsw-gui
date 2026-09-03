using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The two desktop services this application asks the operating system for — opening a
    /// link and writing to the clipboard — wrapped so a failure is a return value rather
    /// than a crash. Both can fail for reasons outside our control: no registered browser,
    /// or another process holding the clipboard open.
    /// </summary>
    public static class SystemShell
    {
        public static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
            }
        }

        public static bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentNullException)
            {
                return false;
            }
        }
    }
}
