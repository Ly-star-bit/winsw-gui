using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Collects everything needed to diagnose a service into one zip: configuration, the
    /// tail of each log, the Windows events, what this console did and what went wrong in it,
    /// and the versions involved.
    /// </summary>
    public static class DiagnosticsBundle
    {
        internal const int TailLines = 2000;
        private const long TailBytes = 2 * 1024 * 1024;

        /// <summary>How many of the service's log files go in at most. See <see cref="PickLogs"/>.</summary>
        internal const int MaxLogFiles = 8;

        /// <summary>
        /// The console's own error log, where it writes the failures it reports and the ones
        /// nobody observed. Worked out here the way the class that writes it works it out, so
        /// that the bundle does not depend on it.
        /// </summary>
        internal static string ErrorLogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "errors.log");

        public static void Create(ServiceEntry entry, string zipPath)
        {
            using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            AddText(zip, "summary.txt", Summary(entry));

            if (entry.ConfigPath != null && File.Exists(entry.ConfigPath))
            {
                AddRedactedConfig(zip, entry.ConfigPath);

                try
                {
                    var model = ServiceConfigModel.Load(entry.ConfigPath);
                    string directory = ConfigPaths.ResolveLogDirectory(model, entry.ConfigPath);
                    string stem = ConfigPaths.ResolveLogBaseName(model, entry.ConfigPath);

                    if (Directory.Exists(directory))
                    {
                        AddLogs(zip, directory, stem, ConfigPaths.ResolveWrapperLogName(entry.ConfigPath));
                    }
                    else
                    {
                        AddText(zip, "logs/README.txt", "The log directory does not exist: " + directory);
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    AddText(zip, "logs/README.txt", "Logs could not be collected: " + e.Message);
                }
            }

            AddConsoleLogs(zip, ActionLog.FilePath, ErrorLogPath);

            AddText(zip, "events.txt", FormatEvents(EventLogReader.Search(entry.ServiceName, entry.DisplayName, 300)));
        }

        /// <summary>
        /// Adds the tails of the service's log files under <c>logs/</c>, and beside them
        /// <c>logs/index.txt</c>: every log file the service has there, and for each one added
        /// the encoding it was read in. The text is re-encoded as UTF-8 whatever the file was
        /// in; the index says what that was, for when the guess was wrong.
        /// </summary>
        internal static void AddLogs(ZipArchive zip, string directory, string stem, string wrapperLogName)
        {
            var found = ConfigPaths.FindLogFiles(directory, stem, wrapperLogName);
            var picked = PickLogs(found, MaxLogFiles);

            var index = new StringBuilder();
            index.AppendLine("Log directory: " + directory);
            index.AppendLine($"The service's log files, newest first: names starting with '{stem}', and the wrapper's log, {wrapperLogName}.");
            index.AppendLine($"The newest file of each kind is included, then the newest of the rest, {MaxLogFiles} at most, each as its");
            index.AppendLine($"last {TailLines:N0} lines and at most {TailBytes / (1024 * 1024)} MB, turned into UTF-8 from the encoding named.");
            index.AppendLine();

            if (found.Count == 0)
            {
                index.AppendLine("(none)");
            }

            int width = Math.Min(60, found.Count == 0 ? 0 : found.Max(f => f.Name.Length));
            foreach (var file in found)
            {
                string note = "not included";
                if (picked.Contains(file))
                {
                    AddText(zip, "logs/" + file.Name, Tail(file.FullName, out string? encoding));
                    note = encoding is null ? "included, could not be read" : "included, read as " + encoding;
                }

                index.AppendLine($"{file.Name.PadRight(width)}  {file.Length,14:N0} bytes  {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}  {note}");
            }

            AddText(zip, "logs/index.txt", index.ToString());
        }

        /// <summary>
        /// Of a service's log files, newest first, the ones the bundle takes, at most
        /// <paramref name="maximum"/>: the newest file of each kind, then the newest of the
        /// rest. Taken newest first and nothing else, the files a busy .out.log leaves behind
        /// as it rolls by size filled the bundle, and the .err.log and the wrapper's log, the
        /// two that say why a service stopped, were left out of it.
        /// </summary>
        internal static IReadOnlyList<FileInfo> PickLogs(IReadOnlyList<FileInfo> newestFirst, int maximum)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            var picked = new HashSet<FileInfo>();
            foreach (var file in newestFirst)
            {
                if (picked.Count < maximum && kinds.Add(KindOf(file.Name)))
                {
                    picked.Add(file);
                }
            }

            foreach (var file in newestFirst)
            {
                if (picked.Count >= maximum)
                {
                    break;
                }

                picked.Add(file);
            }

            return newestFirst.Where(picked.Contains).ToList();
        }

        /// <summary>
        /// Which kind of log a file is: its name without the numbers and dates the rolling
        /// modes put in it, so that <c>svc.3.out.log</c>, <c>svc_20260923.out.log</c> and
        /// <c>svc.out.log</c> are one kind, and <c>svc.err.log</c>, <c>svc.wrapper.log</c> and
        /// <c>svc.out.log.old</c> are each another. Worked out from the name alone rather than
        /// from each log mode's naming, as the viewer finds its files.
        /// </summary>
        internal static string KindOf(string fileName) =>
            string.Join(".", Regex.Matches(fileName, @"\p{L}+").Select(m => m.Value)).ToLowerInvariant();

        /// <summary>
        /// Adds what this console did (<c>actions.log</c>) and what went wrong in it
        /// (<c>errors.log</c>) under <c>console/</c>, each when it exists. Both are this
        /// user's on this machine, and cover every service, not just this one.
        /// </summary>
        internal static void AddConsoleLogs(ZipArchive zip, string actionLogPath, string errorLogPath)
        {
            foreach (string path in new[] { actionLogPath, errorLogPath })
            {
                if (File.Exists(path))
                {
                    AddText(zip, "console/" + Path.GetFileName(path), Tail(path, out _));
                }
            }
        }

        /// <summary>
        /// The events as text, newest first, each with the ID Event Viewer shows. Should the
        /// search have stopped before the first record of the logs, that is said, so that
        /// "no events" is not taken to be about all time.
        /// </summary>
        internal static string FormatEvents(EventSearch search)
        {
            var text = new StringBuilder();
            foreach (var item in search.Events)
            {
                text.AppendLine($"{item.TimeText}  {item.Type,-12} {item.Source}  [{item.EventId}]");
                text.AppendLine(item.Message.Trim());
                text.AppendLine();
            }

            if (search.Scan.CutShortAt is DateTime since)
            {
                text.AppendLine(search.Events.Count == 0
                    ? $"(no events for this service in the last {search.Scan.Examined:N0} records, since {since:yyyy-MM-dd HH:mm:ss}; older records were not searched)"
                    : $"(searched the last {search.Scan.Examined:N0} records, since {since:yyyy-MM-dd HH:mm:ss}; older records were not searched)");
            }

            return text.Length == 0 ? "(no events)" : text.ToString();
        }

        /// <summary>
        /// Adds the configuration with its secrets masked, and beside it the list of what was
        /// masked. A bundle is collected in order to be sent to somebody, and the file it is
        /// built around is allowed to hold a service account's password.
        /// </summary>
        private static void AddRedactedConfig(ZipArchive zip, string configPath)
        {
            string name = Path.GetFileName(configPath);

            string redacted;
            IReadOnlyList<string> removed;
            try
            {
                redacted = ConfigRedactor.Redact(File.ReadAllText(configPath), out removed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AddText(zip, "config/README.txt", "The configuration could not be read: " + e.Message);
                return;
            }

            AddText(zip, "config/" + name, redacted);

            var note = new StringBuilder();
            note.AppendLine("This bundle is meant to be sent to somebody, so " + name + " was rewritten");
            note.AppendLine("before it was added: passwords are replaced with " + ConfigRedactor.Mask + ", and");
            note.AppendLine("credentials in front of a URL's host are removed.");
            note.AppendLine();

            if (removed.Count == 0)
            {
                note.AppendLine("Nothing needed masking.");
            }
            else
            {
                note.AppendLine("Masked:");
                foreach (string item in removed)
                {
                    note.AppendLine("  " + item);
                }
            }

            note.AppendLine();
            note.AppendLine("Two of those rules are guesses, so check the file before sending this on:");
            note.AppendLine();
            note.AppendLine("  * An <env> value is masked when its NAME looks like a secret (PASSWORD,");
            note.AppendLine("    TOKEN, SECRET, KEY and so on). One holding a secret under a name that");
            note.AppendLine("    does not say so is still there.");
            note.AppendLine("  * In <arguments>, <startarguments> and <stoparguments>, only an argument");
            note.AppendLine("    that names itself — '-Dpassword=x', '--api-key x' — is masked. A secret");
            note.AppendLine("    passed positionally, or inside a connection string, is still there.");
            note.AppendLine();
            note.AppendLine("The logs in this bundle, the program's own output and the console's under");
            note.AppendLine("console/, are NOT redacted.");

            AddText(zip, "config/REDACTED.txt", note.ToString());
        }

        private static string Summary(ServiceEntry entry)
        {
            var text = new StringBuilder();
            text.AppendLine($"Collected:        {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            text.AppendLine($"Service:          {entry.ServiceName} ({entry.DisplayName})");
            text.AppendLine($"Status:           {entry.StatusText}   PID {entry.ProcessId}   last exit code {entry.LastExitCodeText}");
            text.AppendLine($"Start mode:       {entry.StartMode}");
            text.AppendLine($"Account:          {entry.Account}");
            text.AppendLine($"Wrapper:          {entry.WrapperPath}");
            text.AppendLine($"Wrapper version:  {entry.WrapperVersion}");
            text.AppendLine($"Configuration:    {entry.ConfigPath}");
            text.AppendLine($"Depends on:       {entry.DependsOnText}");
            text.AppendLine($"Depended by:      {entry.DependedByText}");
            text.AppendLine($"Problem:          {entry.Problem}");
            text.AppendLine();
            text.AppendLine($"OS:               {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            text.AppendLine($"Machine:          {Environment.MachineName}");
            text.AppendLine($".NET:             {Environment.Version}");
            text.AppendLine($"WinSW GUI:        {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");

            try
            {
                if (File.Exists(entry.WrapperPath))
                {
                    var info = FileVersionInfo.GetVersionInfo(entry.WrapperPath);
                    text.AppendLine($"Wrapper product:  {info.ProductName} {info.ProductVersion} ({info.CompanyName})");
                    text.AppendLine($"Wrapper size:     {new FileInfo(entry.WrapperPath).Length:N0} bytes");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            return text.ToString();
        }

        /// <summary>
        /// The last <see cref="TailLines"/> lines, reading at most <see cref="TailBytes"/> from
        /// the end, in the encoding the log viewer would read the file in, which is named in
        /// <paramref name="encoding"/>; null when the file could not be read. A program on
        /// Chinese Windows writes GBK unless told otherwise, and read as UTF-8, as it was here,
        /// every Chinese character became a replacement character in the copy sent on.
        /// </summary>
        internal static string Tail(string path, out string? encoding)
        {
            try
            {
                var tail = LogTailReader.ReadTail(path, TailBytes);
                encoding = tail.EncodingName;

                // Lines before the last so many are left out as well, and counted with the bytes
                // before the tail, so that what is missing is always said.
                string text = tail.Text;
                int start = 0;
                int lines = text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);
                for (int drop = lines - TailLines; drop > 0; drop--)
                {
                    start = text.IndexOf('\n', start) + 1;
                }

                long skipped = tail.SkippedBytes + (tail.Encoding ?? Encoding.UTF8).GetByteCount(text.AsSpan(0, start));
                string header = skipped > 0 ? $"[... {skipped:N0} bytes skipped ...]\r\n" : string.Empty;
                return header + text.Substring(start);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                encoding = null;
                return "(could not read: " + e.Message + ")";
            }
        }

        private static void AddText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
    }
}
