using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The .NET Framework 4.x this machine has, as far as the wrapper's .NET Framework build
    /// cares: the one bundled with this application, and upstream's <c>WinSW-net461.exe</c>.
    /// </summary>
    /// <remarks>
    /// The bundled wrapper targets 4.6.2 and is written out without a <c>.config</c>, so the
    /// runtime never checks the version up front. On a machine with 4.5.x it loads, and then
    /// fails at the first method 4.5 does not have — at install or at the first start, with an
    /// error that says nothing about the framework. Windows Server 2012 R2 ships with 4.5.1,
    /// and a try run of the program itself passes on it, so nothing earlier gives it away.
    /// The self-contained builds carry their own runtime and do not care.
    /// </remarks>
    public static class NetFramework
    {
        /// <summary>
        /// The lowest <c>Release</c> value of 4.6.2: 394802 on Windows 10 1607, 394806
        /// everywhere else. Anything at or above it runs the wrapper's framework build.
        /// </summary>
        public const int Release462 = 394802;

        /// <summary>The file name of Microsoft's .NET Framework 4.8 offline installer, which needs no internet on the server.</summary>
        public const string OfflineInstaller = "ndp48-x86-x64-allos-enu.exe";

        /// <summary>Microsoft's permanent link to <see cref="OfflineInstaller"/>.</summary>
        public const string OfflineInstallerLink = "https://go.microsoft.com/fwlink/?linkid=2088631";

        /// <summary>Where every 4.5 and later install records its <c>Release</c>; 4.0 left no value there.</summary>
        private const string SetupKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

        /// <summary>
        /// The lowest <c>Release</c> of each version, newest first, as Microsoft documents them.
        /// A machine reports its own value, which may be a little higher for the same version.
        /// </summary>
        private static readonly (int Release, string Version)[] Versions =
        {
            (533320, "4.8.1"),
            (528040, "4.8"),
            (461808, "4.7.2"),
            (461308, "4.7.1"),
            (460798, "4.7"),
            (Release462, "4.6.2"),
            (394254, "4.6.1"),
            (393295, "4.6"),
            (379893, "4.5.2"),
            (378675, "4.5.1"),
            (378389, "4.5"),
        };

        private static readonly Lazy<NetFrameworkInfo> InstalledOnce = new(Read);

        /// <summary>
        /// This machine's, read once. Installing a newer framework takes a restart before the
        /// wrapper can use it, and a restart starts this application over as well.
        /// </summary>
        public static NetFrameworkInfo Installed => InstalledOnce.Value;

        /// <summary>
        /// Reads the <c>Release</c> value from the registry. Unknown when the registry cannot be
        /// read at all, which says nothing either way; too old for the wrapper when the key or
        /// the value is missing, which is 4.0 or no 4.x at all.
        /// </summary>
        public static NetFrameworkInfo Read()
        {
            // The tests run elsewhere too, where there is no registry and no .NET Framework
            // for the wrapper to need.
            if (!OperatingSystem.IsWindows())
            {
                return NetFrameworkInfo.Unknown;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SetupKey);
                return FromRegistry(key != null, key?.GetValue("Release"));
            }
            catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
            {
                return NetFrameworkInfo.Unknown;
            }
        }

        /// <summary>The version a <c>Release</c> value stands for, or <c>&lt; 4.5</c> below the first that had one.</summary>
        public static string VersionOf(int release)
        {
            foreach (var (lowest, version) in Versions)
            {
                if (release >= lowest)
                {
                    return version;
                }
            }

            return "< 4.5";
        }

        /// <summary>
        /// What the registry said, as a release: 0 for a missing key or value, which is less
        /// than any 4.5 and later reports. A value of the wrong type is read the same way;
        /// setup has only ever written a DWORD there.
        /// </summary>
        internal static NetFrameworkInfo FromRegistry(bool keyFound, object? release) =>
            new(keyFound && release is int value && value > 0 ? value : 0);
    }

    /// <summary>
    /// A machine's .NET Framework 4.x by its <c>Release</c> value; see <see cref="NetFramework"/>.
    /// </summary>
    /// <param name="Release">
    /// Null when it could not be read, which is taken to be fine, as it was before anything
    /// checked; 0 when 4.5 or later is not installed.
    /// </param>
    public readonly record struct NetFrameworkInfo(int? Release)
    {
        /// <summary>Nothing could be read; nothing is said.</summary>
        public static NetFrameworkInfo Unknown => default;

        public bool IsKnown => this.Release.HasValue;

        /// <summary>
        /// Known to be older than 4.6.2, so the wrapper's framework build will not run here.
        /// An unknown machine is not: the check must never be what stops an install.
        /// </summary>
        public bool TooOldForWrapper => this.Release is { } release && release < NetFramework.Release462;

        /// <summary>The installed version, such as <c>4.5.1</c>; empty when unknown.</summary>
        public string Version => this.Release is { } release ? NetFramework.VersionOf(release) : string.Empty;
    }
}
