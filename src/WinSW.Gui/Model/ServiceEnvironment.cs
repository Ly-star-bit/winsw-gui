using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WinSW.Gui.Model
{
    /// <summary>
    /// One thing <see cref="ServiceConfigModel.CheckEnvironment(string)"/> found: a dictionary key
    /// and the values that go into it, put into words only when shown.
    /// </summary>
    /// <remarks>
    /// Worded through <c>text</c> rather than the localizer directly, as <see cref="RecoveryPlan"/>
    /// is: <c>Localizer.Get</c> in the console, the dictionaries read from source in the tests,
    /// which have no WPF application to look resources up in. Immutable, so that it can be made on
    /// a worker and shown on the UI thread.
    /// </remarks>
    public sealed class EnvironmentFinding
    {
        internal EnvironmentFinding(string key, params string[] values)
        {
            this.Key = key;
            this.Values = values;
        }

        /// <summary>The message's key in the dictionaries, one of the <c>M.Warn.*</c> keys.</summary>
        public string Key { get; }

        /// <summary>What goes into the message's placeholders, in order.</summary>
        public IReadOnlyList<string> Values { get; }

        public string Describe(Func<string, string> text) =>
            string.Format(CultureInfo.CurrentCulture, text(this.Key), this.Values.ToArray<object>());
    }

    /// <summary>
    /// What <see cref="ServiceConfigModel.CheckEnvironment(string)"/> found, and the one fix it can
    /// offer to make itself: a full path in place of a program named without one.
    /// </summary>
    public sealed class EnvironmentCheck
    {
        internal EnvironmentCheck(IReadOnlyList<EnvironmentFinding> findings, string executable, string? fullExecutablePath)
        {
            this.Findings = findings;
            this.Executable = executable;
            this.FullExecutablePath = fullExecutablePath;
        }

        public IReadOnlyList<EnvironmentFinding> Findings { get; }

        /// <summary>
        /// The <c>&lt;executable&gt;</c> the check read, so that the offer is never put in place
        /// of something typed since.
        /// </summary>
        public string Executable { get; }

        /// <summary>
        /// Where the bare <c>&lt;executable&gt;</c> is found on this machine, when a service would
        /// lose it or never find it by that name; null when there is nothing to offer.
        /// </summary>
        public string? FullExecutablePath { get; }
    }

    /// <summary>
    /// What the environment check asks the machine. Behind an interface so that the rules can be
    /// tried against a machine made up for the purpose: the real answers depend on what is
    /// installed where, and the tests also run where there is no Windows at all.
    /// </summary>
    internal interface IServiceMachine
    {
        /// <summary>
        /// The machine's own PATH, as the registry holds it: the one a service is started with,
        /// which never includes any user's.
        /// </summary>
        string MachinePath { get; }

        /// <summary>The PATH the console's own programs, and so a try run, get: this user's as well.</summary>
        string UserPath { get; }

        /// <summary><c>C:\Windows\system32</c>.</summary>
        string SystemDirectory { get; }

        /// <summary><c>C:\Windows</c>.</summary>
        string WindowsDirectory { get; }

        /// <summary>The folder holding the user profiles, <c>C:\Users</c>; null when it cannot be told.</summary>
        string? ProfilesDirectory { get; }

        bool FileExists(string path);

        bool DirectoryExists(string path);

        /// <summary>A small text file's contents; null when it is not there or cannot be read.</summary>
        string? ReadText(string path);

        /// <summary>Whether a drive letter is a network drive mapped in the console's own sign-in.</summary>
        bool IsNetworkDrive(char letter);

        /// <summary>Whether an account exists; null when that cannot be told, as with a domain out of reach.</summary>
        bool? AccountExists(string account);

        /// <summary>
        /// An environment variable's value, as the console has it, for a <c>%NAME%</c> the
        /// configuration does not set itself; null when it is not set.
        /// </summary>
        string? Variable(string name);
    }

    /// <summary>Where <see cref="ProgramSearch"/> found a program given by name alone.</summary>
    internal enum ProgramSource
    {
        /// <summary>Nowhere a service looks, nor on the console's own PATH.</summary>
        NotFound,

        /// <summary>
        /// A folder a service always looks in: beside the wrapper, the working directory, the
        /// Windows folders, a PATH entry inside the Windows folder, or one the configuration's
        /// own <c>&lt;env name="PATH"&gt;</c> adds. None of those can move under the service.
        /// </summary>
        Fixed,

        /// <summary>A folder on the machine's PATH.</summary>
        MachinePath,

        /// <summary>Only on the console's own PATH, which a service does not have.</summary>
        UserPath,

        /// <summary>
        /// A batch file of that name where a service looks: it runs when named in full, never by
        /// the bare name, for which only <c>.exe</c> is tried.
        /// </summary>
        Script,
    }

    /// <summary>
    /// Finds a program given by name alone the way the wrapper's <c>Process.Start</c> finds it.
    /// That does not go through the shell: it hands the command line to <c>CreateProcess</c>,
    /// which appends <c>.exe</c> to a name without an extension (never <c>PATHEXT</c>, which is
    /// the command prompt's) and looks in the wrapper's own folder, the current directory (which
    /// the wrapper sets to the working directory), <c>system32</c>, the 16-bit <c>system</c>
    /// folder, the Windows folder and the PATH, in that order.
    /// </summary>
    internal sealed class ProgramSearch
    {
        private static readonly string[] ScriptExtensions = { ".cmd", ".bat" };

        private readonly IServiceMachine machine;
        private readonly List<(string Folder, ProgramSource Source)> serviceFolders = new();
        private readonly List<string> userFolders;

        /// <param name="wrapperFolder">The wrapper's folder, when known.</param>
        /// <param name="currentFolder">The folder the service runs in, when known.</param>
        /// <param name="servicePath">The PATH the service gets: the machine's, as the configuration's own <c>&lt;env&gt;</c> leaves it.</param>
        public ProgramSearch(IServiceMachine machine, string? wrapperFolder, string? currentFolder, string servicePath)
        {
            this.machine = machine;

            string windows = machine.WindowsDirectory;
            foreach (string? folder in new[]
            {
                wrapperFolder,
                currentFolder,
                machine.SystemDirectory,
                windows.Length == 0 ? null : WindowsPath.Join(windows, "System"),
                windows,
            })
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    this.serviceFolders.Add((folder!, ProgramSource.Fixed));
                }
            }

            var machineFolders = new HashSet<string>(WindowsPath.SplitList(machine.MachinePath), StringComparer.OrdinalIgnoreCase);
            foreach (string folder in WindowsPath.SplitList(servicePath))
            {
                bool moves = machineFolders.Contains(folder) && (windows.Length == 0 || !WindowsPath.IsAtOrUnder(folder, windows));
                this.serviceFolders.Add((folder, moves ? ProgramSource.MachinePath : ProgramSource.Fixed));
            }

            this.userFolders = WindowsPath.SplitList(machine.UserPath).ToList();
        }

        /// <summary>Where <paramref name="name"/>, which has no folder in it, is found, and what that means.</summary>
        public (ProgramSource Source, string? Path) Find(string name)
        {
            string file = WindowsPath.HasExtension(name) ? name : name + ".exe";
            foreach (var (folder, source) in this.serviceFolders)
            {
                if (this.Exists(folder, file) is { } path)
                {
                    return (source, path);
                }
            }

            foreach (string folder in this.userFolders)
            {
                if (this.Exists(folder, file) is { } path)
                {
                    return (ProgramSource.UserPath, path);
                }
            }

            // 'npm', 'yarn' and 'pm2' are batch files: the command prompt finds them through
            // PATHEXT, a service by that name never does.
            if (!WindowsPath.HasExtension(name))
            {
                foreach (string extension in ScriptExtensions)
                {
                    foreach (var (folder, _) in this.serviceFolders)
                    {
                        if (this.Exists(folder, name + extension) is { } path)
                        {
                            return (ProgramSource.Script, path);
                        }
                    }

                    foreach (string folder in this.userFolders)
                    {
                        if (this.Exists(folder, name + extension) is { } path)
                        {
                            return (ProgramSource.UserPath, path);
                        }
                    }
                }
            }

            return (ProgramSource.NotFound, null);
        }

        private string? Exists(string folder, string file)
        {
            string path = WindowsPath.Join(folder, file);
            return this.machine.FileExists(path) ? path : null;
        }
    }

    /// <summary>
    /// <c>%NAME%</c> expanded the way <c>ExpandEnvironmentStrings</c>, and so the wrapper, expands
    /// it, with the variables looked up wherever the caller says.
    /// </summary>
    internal static class WindowsEnvironment
    {
        /// <summary>
        /// Expands every <c>%NAME%</c> in <paramref name="value"/>. A name that is not set stays as
        /// it is, <c>%</c> signs and all, and its closing <c>%</c> may open the next name; what a
        /// name stands for is not expanded again.
        /// </summary>
        /// <param name="value">The text to expand.</param>
        /// <param name="lookup">
        /// A variable's value, and whether that is known for certain; null when it is not set.
        /// </param>
        /// <returns>
        /// The expanded text, and whether every name in it was set and known: false when any stays
        /// as <c>%NAME%</c>, or stands for a value that is not known itself.
        /// </returns>
        public static (string Text, bool Known) Expand(string value, Func<string, (string Value, bool Known)?> lookup)
        {
            var text = new StringBuilder(value.Length);
            bool known = true;
            int at = 0;
            while (true)
            {
                int open = value.IndexOf('%', at);
                int close = open < 0 ? -1 : value.IndexOf('%', open + 1);
                if (close < 0)
                {
                    text.Append(value, at, value.Length - at);
                    return (text.ToString(), known);
                }

                text.Append(value, at, open - at);
                string name = value.Substring(open + 1, close - open - 1);
                if (name.Length > 0 && lookup(name) is { } found)
                {
                    text.Append(found.Value);
                    known &= found.Known;
                    at = close + 1;
                }
                else
                {
                    // Kept as it is, and the closing % may open the next name. "%%" names
                    // nothing, so it is two percent signs rather than a name not set.
                    text.Append('%').Append(name);
                    known &= name.Length == 0;
                    at = close;
                }
            }
        }
    }

    /// <summary>
    /// Windows path rules, spelled out. The check reasons about paths on the server the service
    /// runs on, and <see cref="Path"/> follows the rules of whatever machine it happens to run on.
    /// </summary>
    internal static class WindowsPath
    {
        private static readonly char[] Separators = { '\\', '/' };

        /// <summary>Folders under the profiles folder that belong to nobody in particular.</summary>
        private static readonly string[] SharedProfiles = { "Public", "Default", "Default User", "All Users" };

        /// <summary>A name with no folder in it at all, which <c>CreateProcess</c> goes looking for.</summary>
        public static bool IsBare(string path) => path.IndexOfAny(Separators) < 0 && path.IndexOf(':') < 0;

        /// <summary>As <c>Path.IsPathRooted</c> is on Windows: a drive, a share, or a leading separator.</summary>
        public static bool IsRooted(string path) =>
            (path.Length > 0 && Array.IndexOf(Separators, path[0]) >= 0) || DriveLetter(path) != null;

        /// <summary>The drive a path is on, upper case; null for a share or a relative path.</summary>
        public static char? DriveLetter(string path) =>
            path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]) ? char.ToUpperInvariant(path[0]) : null;

        /// <summary>Whether a path is on a share, <c>\\server\share\…</c>, rather than a local disk.</summary>
        public static bool IsUnc(string path)
        {
            string normal = Normalize(path);
            if (normal.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normal.StartsWith(@"\\", StringComparison.Ordinal)
                && !normal.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !normal.StartsWith(@"\\.\", StringComparison.Ordinal);
        }

        public static string Join(string folder, string name) => folder.TrimEnd(Separators) + "\\" + name;

        /// <summary>The folder a path is in; null for a drive's root or a bare name.</summary>
        public static string? Parent(string path)
        {
            string trimmed = path.TrimEnd(Separators);
            int cut = trimmed.LastIndexOfAny(Separators);
            if (cut <= 0)
            {
                return null;
            }

            // "C:\app.exe" is in "C:\"; "C:" alone would be that drive's current directory.
            string parent = trimmed.Substring(0, cut);
            return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
        }

        /// <summary>As <c>Path.HasExtension</c>: a dot in the last part, with something after it.</summary>
        public static bool HasExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot > path.LastIndexOfAny(Separators) && dot < path.Length - 1;
        }

        /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or anywhere inside it.</summary>
        public static bool IsAtOrUnder(string path, string folder)
        {
            string normalPath = Normalize(path).TrimEnd('\\');
            string normalFolder = Normalize(folder).TrimEnd('\\');
            return normalPath.Equals(normalFolder, StringComparison.OrdinalIgnoreCase)
                || normalPath.StartsWith(normalFolder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The user profile a path is inside, <c>C:\Users\name</c>, or null when it is in none;
        /// <c>Public</c> and the templates belong to nobody and do not count.
        /// </summary>
        public static string? ProfileFolder(string path, string profilesDirectory)
        {
            string root = Normalize(profilesDirectory).TrimEnd('\\') + "\\";
            string normal = Normalize(path);
            if (!normal.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rest = normal.Substring(root.Length);
            int cut = rest.IndexOf('\\');
            string name = cut < 0 ? rest : rest.Substring(0, cut);
            return name.Length == 0 || SharedProfiles.Contains(name, StringComparer.OrdinalIgnoreCase) ? null : root + name;
        }

        /// <summary>The folders of a PATH value, in order, empty entries left out.</summary>
        public static IEnumerable<string> SplitList(string? list) =>
            (list ?? string.Empty)
                .Split(';')
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0);

        private static string Normalize(string path) => path.Replace('/', '\\');
    }

    /// <summary>The machine the console runs on, asked as a service on it would find things.</summary>
    internal sealed class ServiceMachine : IServiceMachine
    {
        public static readonly ServiceMachine Local = new();

        private ServiceMachine()
        {
        }

        /// <remarks>
        /// Read from the registry each time, not from this process: a PATH changed since the
        /// console started is what a service installed now is going to be given.
        /// </remarks>
        public string MachinePath => Read(EnvironmentVariableTarget.Machine);

        public string UserPath => Read(EnvironmentVariableTarget.Process) + ";" + Read(EnvironmentVariableTarget.User);

        public string SystemDirectory => Environment.SystemDirectory;

        public string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        public string? ProfilesDirectory
        {
            get
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                    if (key?.GetValue("ProfilesDirectory") is string folder && folder.Length > 0)
                    {
                        return folder;
                    }
                }
                catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
                {
                    // Fall back on where this user's own profile is.
                }

                string own = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return own.Length == 0 ? null : Path.GetDirectoryName(own);
            }
        }

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public string? ReadText(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public bool IsNetworkDrive(char letter)
        {
            try
            {
                return new DriveInfo(letter + ":\\").DriveType == DriveType.Network;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public string? Variable(string name) => Environment.GetEnvironmentVariable(name);

        public bool? AccountExists(string account)
        {
            try
            {
                new NTAccount(account).Translate(typeof(SecurityIdentifier));
                return true;
            }
            catch (IdentityNotMappedException)
            {
                return false;
            }
            catch (SystemException)
            {
                // Domain unreachable: cannot tell either way.
                return null;
            }
        }

        private static string Read(EnvironmentVariableTarget target)
        {
            try
            {
                return Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty;
            }
            catch (SecurityException)
            {
                return string.Empty;
            }
        }
    }
}
