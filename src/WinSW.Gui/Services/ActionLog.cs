using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A record of what this console did to the machine: which service was started, stopped,
    /// uninstalled or upgraded, which configuration was saved, when, and how it went.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per user, under <c>%LOCALAPPDATA%\WinSW.Gui</c> beside the settings, so it can always be
    /// written without elevation. One line per operation, tab-separated, so it reads in a text
    /// editor and loads into a spreadsheet as it is.
    /// </para>
    /// <para>
    /// The outcomes are written in English whatever the interface language — ok, failed,
    /// declined, timed out — so that a log from one machine reads the same as a log from
    /// another. The detail after a failure is the message the console showed at the time.
    /// </para>
    /// <para>
    /// A log nobody has to maintain is one that cannot fill the disk: past
    /// <see cref="MaxBytes"/> the file is moved aside to <c>actions.1.log</c>, replacing the
    /// one before, and a new one is begun.
    /// </para>
    /// </remarks>
    public static class ActionLog
    {
        internal const long MaxBytes = 1024 * 1024;

        private static readonly object Gate = new();

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "actions.log");

        /// <summary>Records an operation that ran through the wrapper, with its result.</summary>
        public static void Record(string action, string target, CommandResult result) =>
            Record(action, target, Describe(result));

        /// <summary>
        /// Records one operation. Never throws: losing a line of the log is better than failing
        /// the operation it describes, which has already happened.
        /// </summary>
        public static void Record(string action, string target, string outcome) =>
            Append(FilePath, Format(DateTime.Now, CurrentUser, action, target, outcome));

        /// <summary>Opens the log, or its folder when nothing has been recorded yet.</summary>
        public static void Open() => OpenLogFile(FilePath);

        internal static string Describe(CommandResult result) => result switch
        {
            { Cancelled: true } => "declined",
            { TimedOut: true } => "timed out",
            { Succeeded: true } => "ok",
            _ => "failed: " + (result.Error ?? "exit code " + result.ExitCode.ToString(CultureInfo.InvariantCulture)),
        };

        /// <summary>
        /// One line. Tabs and line breaks inside a field — an error message can carry either —
        /// are flattened to spaces, so that one operation is always one line of five fields.
        /// </summary>
        internal static string Format(DateTime at, string user, string action, string target, string outcome) =>
            string.Join(
                "\t",
                at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Flatten(user),
                Flatten(action),
                Flatten(target),
                Flatten(outcome));

        internal static void Append(string path, string line)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                    var existing = new FileInfo(path);
                    if (existing.Exists && existing.Length >= MaxBytes)
                    {
                        File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
                    }

                    File.AppendAllText(path, line + Environment.NewLine, Utf8);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>
        /// A log in whatever opens .log files, or its folder when nothing has been recorded
        /// yet, so the button always shows where the record will be. Shared with
        /// <see cref="ErrorLog"/>, which keeps its file the same way.
        /// </summary>
        internal static void OpenLogFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }

                string folder = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // No program for .log files, or the folder cannot be created; nothing to show.
            }
        }

        private static string Flatten(string value) =>
            value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        private static string CurrentUser => Environment.UserDomainName + "\\" + Environment.UserName;
    }
}
