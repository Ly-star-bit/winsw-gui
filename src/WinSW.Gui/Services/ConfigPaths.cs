using System;
using System.IO;
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
    }
}
