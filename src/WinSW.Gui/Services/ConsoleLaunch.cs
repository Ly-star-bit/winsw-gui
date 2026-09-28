using System;
using System.IO;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Who a second launch is, as it tells the copy already running in the session: its
    /// version, the executable it was started from, and the configuration it was asked to open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second launch used to wake the running copy and exit without saying what it was. A
    /// newer executable, saved under another name and double-clicked while the old one watched
    /// from the tray, brought the old one forward — and the user took that for the update having
    /// worked. Told who the launch is, the running copy can see that it is not the same console
    /// and offer to hand over to it; see <see cref="Replacement"/>.
    /// </para>
    /// <para>
    /// On the wire it is one line of three tab-separated fields, the last of them empty when
    /// there is no configuration. A tab cannot appear in a Windows path, so the line cannot be
    /// taken for the bare configuration path <see cref="ConfigHandoff"/> also accepts.
    /// </para>
    /// </remarks>
    public sealed class ConsoleLaunch
    {
        private const char Separator = '\t';

        public ConsoleLaunch(string version, string executablePath, string? configPath)
        {
            this.Version = version;
            this.ExecutablePath = executablePath;
            this.ConfigPath = configPath;
        }

        /// <summary>The launch's version, as <see cref="UpdateChecker.CurrentGuiVersion"/> gives it.</summary>
        public string Version { get; }

        /// <summary>The executable the launch was started from, as a full path.</summary>
        public string ExecutablePath { get; }

        /// <summary>The configuration the launch was asked to open, as a full path; null for none.</summary>
        public string? ConfigPath { get; }

        /// <summary>
        /// The line sent for this launch. A field is taken as it is: none of them can hold a tab
        /// or a line break, a path because Windows does not allow one and a version because the
        /// build does not write one.
        /// </summary>
        public string ToRequest() => string.Join(Separator, this.Version, this.ExecutablePath, this.ConfigPath ?? string.Empty);

        /// <summary>
        /// Reads a line <see cref="ToRequest"/> wrote. Null for anything else: no tab, the wrong
        /// number of fields, an empty version, an executable that is not a full path to an .exe,
        /// or a configuration that is not a full path to an .xml.
        /// </summary>
        /// <remarks>
        /// The running copy may start the executable named here, so it is held to what a
        /// launch's own path can be. The pipe it comes through takes connections from this
        /// account at this elevation only, which could start that executable anyway; the check is
        /// against a line that is garbled, not against a caller.
        /// </remarks>
        public static ConsoleLaunch? FromRequest(string line)
        {
            string[] fields = line.Split(Separator);
            if (fields.Length != 3)
            {
                return null;
            }

            string version = fields[0];
            string executable = fields[1];
            string config = fields[2];

            if (version.Length == 0 || HasControlCharacter(version) || !IsExecutablePath(executable))
            {
                return null;
            }

            if (config.Length > 0 && !ConfigHandoff.IsConfigurationPath(config))
            {
                return null;
            }

            return new ConsoleLaunch(version, executable, config.Length > 0 ? config : null);
        }

        /// <summary>
        /// True when the launch is the console already running: the same executable, at the
        /// same version. Anything else — another path, or the same path now holding another
        /// version — is a console the running one can offer to hand over to.
        /// </summary>
        /// <param name="version">The running copy's version.</param>
        /// <param name="executablePath">The running copy's executable; null when it cannot tell, which counts as the same.</param>
        public bool IsSameConsole(string version, string? executablePath) =>
            executablePath is null
            || (string.Equals(this.Version, version, StringComparison.OrdinalIgnoreCase) && IsSamePath(this.ExecutablePath, executablePath));

        /// <summary>
        /// Two paths to the same file, as far as the text tells: Windows paths are compared
        /// without regard to case, and <c>.</c> and <c>..</c> are resolved first.
        /// </summary>
        internal static bool IsSamePath(string path, string other)
        {
            try
            {
                return string.Equals(Path.GetFullPath(path), Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return string.Equals(path, other, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsExecutablePath(string path) =>
            path.Length > 0
            && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && !HasControlCharacter(path)
            && Path.IsPathFullyQualified(path)
            && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        private static bool HasControlCharacter(string text)
        {
            foreach (char c in text)
            {
                if (char.IsControl(c))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
