using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Collects everything needed to diagnose a service into one zip: configuration, the
    /// tail of each log, the Windows events, and the versions involved.
    /// </summary>
    public static class DiagnosticsBundle
    {
        private const int TailLines = 2000;
        private const long TailBytes = 2 * 1024 * 1024;

        public static void Create(ServiceEntry entry, string zipPath)
        {
            using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

            AddText(zip, "summary.txt", Summary(entry));

            if (entry.ConfigPath != null && File.Exists(entry.ConfigPath))
            {
                zip.CreateEntryFromFile(entry.ConfigPath, "config/" + Path.GetFileName(entry.ConfigPath));

                try
                {
                    var model = ServiceConfigModel.Load(entry.ConfigPath);
                    string directory = ConfigPaths.ResolveLogDirectory(model, entry.ConfigPath);
                    string stem = ConfigPaths.ResolveLogBaseName(model, entry.ConfigPath);

                    if (Directory.Exists(directory))
                    {
                        foreach (var file in new DirectoryInfo(directory).EnumerateFiles(stem + "*.log").OrderByDescending(f => f.LastWriteTime).Take(8))
                        {
                            AddText(zip, "logs/" + file.Name, Tail(file.FullName));
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    AddText(zip, "logs/README.txt", "Logs could not be collected: " + e.Message);
                }
            }

            var events = EventLogReader.Read(entry.ServiceName, entry.DisplayName, 300);
            var text = new StringBuilder();
            foreach (var item in events)
            {
                text.AppendLine($"{item.TimeText}  {item.Type,-12} {item.Source}  [{item.EventId}]");
                text.AppendLine(item.Message.Trim());
                text.AppendLine();
            }

            AddText(zip, "events.txt", text.Length == 0 ? "(no events)" : text.ToString());
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

        /// <summary>The last <see cref="TailLines"/> lines, reading at most <see cref="TailBytes"/> from the end.</summary>
        private static string Tail(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = stream.Length;
                long start = Math.Max(0, length - TailBytes);
                stream.Position = start;

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lines = reader.ReadToEnd().Split('\n');
                var kept = lines.Skip(Math.Max(0, lines.Length - TailLines));
                string header = start > 0 ? $"[... {start:N0} bytes skipped ...]\r\n" : string.Empty;
                return header + string.Join("\n", kept);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
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
