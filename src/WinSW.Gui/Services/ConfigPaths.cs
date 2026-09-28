using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Resolves the paths a configuration refers to, the same way the wrapper does at run time.
    /// </summary>
    public static class ConfigPaths
    {
        /// <summary>
        /// Expands a configuration value. The wrapper publishes <c>%BASE%</c> as the directory
        /// holding the configuration file before expanding anything else, so the GUI has to
        /// substitute it itself: the variable does not exist in this process.
        /// </summary>
        public static string Expand(string value, string configPath)
        {
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty;

            string expanded = value
                .Replace("%BASE%", baseDirectory, StringComparison.OrdinalIgnoreCase)
                .Replace("%SERVICE_ID%", Path.GetFileNameWithoutExtension(configPath), StringComparison.OrdinalIgnoreCase);

            return Environment.ExpandEnvironmentVariables(expanded);
        }

        /// <summary>
        /// The directory the wrapper writes logs to: <c>&lt;logpath&gt;</c> when set,
        /// otherwise the directory holding the configuration file.
        /// </summary>
        public static string ResolveLogDirectory(ServiceConfigModel model, string configPath)
        {
            string fallback = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

            if (string.IsNullOrWhiteSpace(model.LogPath))
            {
                return fallback;
            }

            try
            {
                string resolved = Expand(model.LogPath!, configPath);
                return Path.IsPathRooted(resolved) ? resolved : Path.Combine(fallback, resolved);
            }
            catch (ArgumentException)
            {
                return fallback;
            }
        }

        /// <summary>
        /// The directory the service's process runs in: <c>&lt;workingdirectory&gt;</c> when
        /// set, otherwise the directory holding the configuration file, which is the
        /// wrapper's own fallback and the one <see cref="TrialRunner"/> uses.
        /// </summary>
        /// <remarks>
        /// A bare relative path is a guess rather than a reading. The wrapper assigns the
        /// value straight to <see cref="Environment.CurrentDirectory"/> without ever setting
        /// one of its own, so a service resolves it against <c>system32</c> — never what
        /// anybody meant. It is combined with the configuration's directory instead, which is
        /// the same guess <see cref="ResolveLogDirectory"/> makes and the only one that can
        /// open a folder somebody wants to look at.
        /// </remarks>
        public static string ResolveWorkingDirectory(ServiceConfigModel model, string configPath)
        {
            string fallback = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

            if (string.IsNullOrWhiteSpace(model.WorkingDirectory))
            {
                return fallback;
            }

            try
            {
                string resolved = Expand(model.WorkingDirectory!, configPath);
                return Path.IsPathRooted(resolved) ? resolved : Path.Combine(fallback, resolved);
            }
            catch (ArgumentException)
            {
                return fallback;
            }
        }

        /// <summary>
        /// The stem every log file for this service starts with: <c>&lt;logname&gt;</c> when
        /// set, otherwise the configuration file's base name.
        /// </summary>
        public static string ResolveLogBaseName(ServiceConfigModel model, string configPath) =>
            string.IsNullOrWhiteSpace(model.LogName)
                ? Path.GetFileNameWithoutExtension(configPath)
                : model.LogName!;

        /// <summary>
        /// The name of the wrapper's own log, <c>&lt;name&gt;.wrapper.log</c> in the log
        /// directory. The wrapper names it after the configuration file whatever
        /// <c>&lt;logname&gt;</c> says, which names only the program's output, so a service
        /// with a log name of its own has a wrapper log that does not start with it.
        /// </summary>
        public static string ResolveWrapperLogName(string configPath) =>
            Path.GetFileNameWithoutExtension(configPath) + ".wrapper.log";

        /// <summary>
        /// A service's log files in <paramref name="directory"/>, newest first and each once:
        /// the .log and .txt files whose names start with <paramref name="stem"/>, which are
        /// the program's output under every log mode, numbered or dated once rolled; the
        /// .log.old files roll mode sets aside at each start, which hold the run before the
        /// current one; and the wrapper's own log, <paramref name="wrapperLogName"/>, which
        /// starts with the stem only while <c>&lt;logname&gt;</c> is left unset.
        /// </summary>
        public static IReadOnlyList<FileInfo> FindLogFiles(string directory, string stem, string wrapperLogName)
        {
            var info = new DirectoryInfo(directory);
            var found = info
                .EnumerateFiles(stem + "*")
                .Where(IsLogFile)
                .ToList();

            var wrapperLog = new FileInfo(Path.Combine(info.FullName, wrapperLogName));
            if (wrapperLog.Exists && !found.Any(f => string.Equals(f.FullName, wrapperLog.FullName, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(wrapperLog);
            }

            return found.OrderByDescending(f => f.LastWriteTime).ToList();
        }

        private static bool IsLogFile(FileInfo file) =>
            file.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || file.Name.EndsWith(".log.old", StringComparison.OrdinalIgnoreCase);
    }
}
