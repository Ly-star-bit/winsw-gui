using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The WinSW executable that ships inside this application.
    /// </summary>
    /// <remarks>
    /// A service needs a wrapper on disk: the service control manager starts a service by
    /// launching the executable named in its <c>ImagePath</c>, and that process is the one
    /// that registers with the SCM and stays alive for the service's lifetime. A desktop
    /// application cannot be that process — it is not running at boot, or with nobody logged
    /// in. Carrying a copy means the wizard can install a service on a machine that has never
    /// seen WinSW and has no way to reach GitHub.
    /// </remarks>
    public static class BundledWrapper
    {
        private const string ResourceName = "WinSW.Gui.Wrapper.exe";

        private static string? extractedPath;
        private static string? version;

        /// <summary>
        /// False in a build made without the wrapper alongside it, in which case the wizard
        /// falls back to downloading or picking one.
        /// </summary>
        public static bool IsAvailable => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null;

        /// <summary>
        /// The wrapper's file version, or null when it cannot be determined.
        /// </summary>
        /// <remarks>
        /// The file version, not the product version, because it is what an installed
        /// service reports and therefore the only version the two can be compared on. Its
        /// revision counts the commits that have touched the wrapper, so a wrapper newer
        /// than the last upstream release — which this one is — says so.
        /// </remarks>
        public static string? Version
        {
            get
            {
                if (version != null)
                {
                    return version;
                }

                return Extract() is { } path ? version = FileVersionInfo.GetVersionInfo(path).FileVersion : null;
            }
        }

        /// <summary>
        /// Writes the wrapper into a per-user cache and returns its path, or null when this
        /// build carries none. The caller copies it to wherever the service will live; the
        /// cache exists so the version can be read without asking for a destination first.
        /// </summary>
        public static string? Extract()
        {
            if (extractedPath != null && File.Exists(extractedPath))
            {
                return extractedPath;
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return null;
            }

            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WinSW.Gui");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, "WinSW.exe");

                // A stale copy from an older release of this application is replaced; the
                // length is enough to tell them apart and costs nothing to check.
                if (!File.Exists(path) || new FileInfo(path).Length != stream.Length)
                {
                    using var file = File.Create(path);
                    stream.CopyTo(file);
                }

                return extractedPath = path;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A locked or unwritable cache is not fatal: the caller reports it and the
                // user can still point at a wrapper of their own.
                return null;
            }
        }
    }
}
