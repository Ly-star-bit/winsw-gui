using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace WinSW.Gui.Services
{
    /// <summary>Where the Python that runs a picked <c>.py</c> script comes from.</summary>
    public enum PythonInterpreterSource
    {
        /// <summary>The program is not a script, so nothing has to run it.</summary>
        None,

        /// <summary>The <c>python.exe</c> of the virtual environment nearest the script.</summary>
        VirtualEnvironment,

        /// <summary>A <c>python.exe</c> on the machine's PATH, the one a service sees.</summary>
        SystemPath,

        /// <summary>Neither; the service would run a bare <c>python</c> and fail to start.</summary>
        NotFound,
    }

    /// <summary>What a working directory turns out to be, as far as Python is concerned.</summary>
    public enum PythonFolder
    {
        /// <summary>Nothing to do with Python, as far as can be told.</summary>
        None,

        /// <summary>A virtual environment's <c>Scripts</c> folder: launchers and the interpreter.</summary>
        VirtualEnvironmentScripts,

        /// <summary>A Python installation's <c>Scripts</c> folder: launchers pip put there.</summary>
        InstallationScripts,

        /// <summary>The top of a virtual environment, the folder holding <c>pyvenv.cfg</c>.</summary>
        VirtualEnvironment,

        /// <summary>A Python installation, the folder holding <c>python.exe</c>.</summary>
        Installation,
    }

    /// <summary>
    /// What the wizard learned about a program from where it sits: whether Python runs it, the
    /// project a virtual environment belongs to, and for a script, the interpreter to run it with.
    /// </summary>
    /// <remarks>
    /// Read once when the program changes. The wizard asks for these on every keystroke in the
    /// arguments box, and the answers come from the file system.
    /// </remarks>
    public sealed class PythonTarget
    {
        /// <summary>A program Python has nothing to do with, or no program at all.</summary>
        public static readonly PythonTarget None = new(false, null, null, PythonInterpreterSource.None);

        public PythonTarget(bool isPython, string? projectRoot, string? interpreter, PythonInterpreterSource interpreterSource)
        {
            this.IsPython = isPython;
            this.ProjectRoot = projectRoot;
            this.Interpreter = interpreter;
            this.InterpreterSource = interpreterSource;
        }

        /// <summary>
        /// Python runs this program: a script, an interpreter, or a launcher pip installed
        /// into a virtual environment or a Python installation.
        /// </summary>
        public bool IsPython { get; }

        /// <summary>The program is a script, which <see cref="Interpreter"/> has to run.</summary>
        public bool IsScript => this.InterpreterSource != PythonInterpreterSource.None;

        /// <summary>
        /// The folder the program's virtual environment was made in, which is where its code
        /// is; null when the program does not belong to a virtual environment.
        /// </summary>
        public string? ProjectRoot { get; }

        /// <summary>For a script, the executable to write in its place; otherwise null.</summary>
        public string? Interpreter { get; }

        /// <summary>Where <see cref="Interpreter"/> was found, which the wizard's hint names.</summary>
        public PythonInterpreterSource InterpreterSource { get; }
    }

    /// <summary>
    /// Recognises Python programs by how they are laid out on disk, so the wizard can set them
    /// up the way they have to run under a service.
    /// </summary>
    /// <remarks>
    /// A Python service goes wrong in ways the program's own folder does not show. What gets
    /// picked is usually a launcher — <c>.venv\Scripts\uvicorn.exe</c>, or <c>python.exe</c>
    /// itself — and its folder is the one place the application's code is not: started there,
    /// <c>uvicorn main:app</c> cannot import <c>main</c>, and the default recovery restarts it
    /// for ever. The code is in the folder the virtual environment was made in.
    /// </remarks>
    public static class PythonProject
    {
        /// <summary>
        /// Variables worth setting for any Python program run as a service.
        /// </summary>
        /// <remarks>
        /// Writing to a pipe rather than a console, Python buffers its output in blocks of
        /// several kilobytes, so the log shows nothing for minutes and loses the last lines when
        /// the process is killed; PYTHONUNBUFFERED writes each line as it comes. And with no
        /// console to take its encoding from, it encodes output in the ANSI code page, where
        /// printing anything outside it raises UnicodeEncodeError and takes the program down;
        /// PYTHONIOENCODING makes it UTF-8, which carries any character and which the log
        /// viewer recognises by itself.
        /// </remarks>
        public static readonly IReadOnlyList<(string Name, string Value)> RecommendedEnvironment = new[]
        {
            ("PYTHONUNBUFFERED", "1"),
            ("PYTHONIOENCODING", "utf-8"),
        };

        /// <summary>
        /// How many folders, starting with the script's own, are searched for a virtual
        /// environment. A project nests its code a few levels deep at most, and every level
        /// costs a handful of file checks on the UI thread.
        /// </summary>
        private const int SearchDepth = 5;

        /// <summary>
        /// The names a virtual environment is given in practice: what <c>python -m venv</c>
        /// examples, uv, Poetry and editors create. Asking for these few is cheap; listing
        /// every folder on the way up is not, on a share.
        /// </summary>
        private static readonly string[] VirtualEnvironmentNames = { ".venv", "venv", "env" };

        /// <summary><c>module:attribute</c>, as uvicorn, gunicorn, hypercorn and waitress take an application.</summary>
        private static readonly Regex ModuleReference = new(
            @"^(?<module>[A-Za-z_]\w*(\.[A-Za-z_]\w*)*):[A-Za-z_][\w.]*(\(.*\))?$",
            RegexOptions.CultureInvariant);

        /// <summary>A dotted module name, as <c>python -m</c> takes one.</summary>
        private static readonly Regex ModuleName = new(@"^[A-Za-z_]\w*(\.[A-Za-z_]\w*)*$", RegexOptions.CultureInvariant);

        /// <summary><c>python.exe</c>, <c>pythonw.exe</c>, <c>python3.12.exe</c>, the <c>py</c> launcher; with or without the extension.</summary>
        private static readonly Regex InterpreterName = new(
            @"^(pythonw?(\d+(\.\d+)*)?|pyw?)(\.exe)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>The path names a Python script, which is not something Windows can start by itself.</summary>
        public static bool IsScript(string path) =>
            string.Equals(Path.GetExtension(path.Trim()), ".py", StringComparison.OrdinalIgnoreCase);

        /// <summary>Everything the wizard needs to know about <paramref name="target"/>; see <see cref="PythonTarget"/>.</summary>
        public static PythonTarget Inspect(string target) => Inspect(target, MachinePath);

        /// <param name="target">The program picked in the wizard.</param>
        /// <param name="machinePath">Reads the PATH a service sees; asked only when a script has no virtual environment.</param>
        internal static PythonTarget Inspect(string target, Func<string?> machinePath)
        {
            string trimmed = target.Trim();
            if (trimmed.Length == 0)
            {
                return PythonTarget.None;
            }

            if (IsScript(trimmed))
            {
                // A script is run by the environment it was written for, where its packages
                // are installed; only without one does the machine's own Python get to try.
                if (Path.IsPathRooted(trimmed)
                    && Path.GetDirectoryName(trimmed) is { } folder
                    && FindVirtualEnvironment(folder) is { } environment)
                {
                    return new PythonTarget(
                        true,
                        Path.GetDirectoryName(environment),
                        Path.Combine(environment, "Scripts", "python.exe"),
                        PythonInterpreterSource.VirtualEnvironment);
                }

                return FindOnPath(machinePath(), "python.exe") is { } python
                    ? new PythonTarget(true, null, python, PythonInterpreterSource.SystemPath)
                    : new PythonTarget(true, null, "python", PythonInterpreterSource.NotFound);
            }

            if (Path.IsPathRooted(trimmed) && Path.GetDirectoryName(trimmed) is { } directory)
            {
                switch (ClassifyScripts(directory))
                {
                    case PythonFolder.VirtualEnvironmentScripts:
                        return new PythonTarget(true, Path.GetDirectoryName(Path.GetDirectoryName(directory)), null, PythonInterpreterSource.None);
                    case PythonFolder.InstallationScripts:
                        return new PythonTarget(true, null, null, PythonInterpreterSource.None);
                }
            }

            return InterpreterName.IsMatch(Path.GetFileName(trimmed))
                ? new PythonTarget(true, null, null, PythonInterpreterSource.None)
                : PythonTarget.None;
        }

        /// <summary>
        /// The folder a program picked by its path should work in, when nothing else is known
        /// about it: for a launcher or the interpreter in a virtual environment's <c>Scripts</c>
        /// folder, the project the environment was made in; for anything else, a script
        /// included, the program's own folder. Null for a path with no folder.
        /// </summary>
        /// <remarks>
        /// For the editor's Browse, which has only the program to go on. Its folder was taken as
        /// it was, and <c>.venv\Scripts</c> is the one place the application's code is not (see
        /// the class remarks). The wizard reads the arguments as well, and prefers the folder of
        /// a script they name.
        /// </remarks>
        public static string? WorkingDirectoryFor(string program)
        {
            string trimmed = program.Trim();
            string? folder = Path.GetDirectoryName(trimmed);

            // A script works in its own folder, as in the wizard. Its ProjectRoot is where the
            // environment that runs it was found, which may be some levels further up.
            return IsScript(trimmed) ? folder : Inspect(trimmed).ProjectRoot ?? folder;
        }

        /// <summary>
        /// The virtual environment nearest <paramref name="directory"/>: one of the usual
        /// names in the folder itself or in one of the few above it, with an interpreter in it.
        /// </summary>
        public static string? FindVirtualEnvironment(string directory)
        {
            string? current = directory;
            for (int level = 0; level < SearchDepth && !string.IsNullOrEmpty(current); level++)
            {
                foreach (string name in VirtualEnvironmentNames)
                {
                    string candidate = Path.Combine(current, name);
                    if (File.Exists(Path.Combine(candidate, "pyvenv.cfg"))
                        && File.Exists(Path.Combine(candidate, "Scripts", "python.exe")))
                    {
                        return candidate;
                    }
                }

                current = Path.GetDirectoryName(current);
            }

            return null;
        }

        /// <summary>The first <paramref name="fileName"/> in the folders a PATH value lists, or null.</summary>
        internal static string? FindOnPath(string? path, string fileName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string folder = entry.Trim().Trim('"');
                if (folder.Length == 0 || !Path.IsPathRooted(folder))
                {
                    continue;
                }

                // The python.exe in WindowsApps is the Microsoft Store's alias: run, it opens
                // the Store. It is normally on the user's PATH only, but nothing a service
                // could use ever lives there.
                if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), "WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string candidate = Path.Combine(folder, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// What the interpreter is given for a script: the script, quoted, ahead of the
        /// arguments. Arguments that already name the script are a Python command line written
        /// out in full — interpreter options such as <c>-X utf8</c> in front of it — and are
        /// handed over as they are.
        /// </summary>
        public static string ScriptArguments(string scriptPath, string arguments)
        {
            string script = scriptPath.Trim();
            string rest = arguments.Trim();
            string name = Path.GetFileName(script);
            if (ServiceDiscovery.SplitCommandLine(rest).Any(token => string.Equals(Path.GetFileName(token), name, StringComparison.OrdinalIgnoreCase)))
            {
                return rest;
            }

            string quoted = "\"" + script + "\"";
            return rest.Length == 0 ? quoted : quoted + " " + rest;
        }

        /// <summary>
        /// Whether an argument names something in <paramref name="directory"/>: a relative
        /// file such as <c>main.py</c> or <c>app\main.py</c>, or a <c>module:app</c> such as
        /// <c>main:app</c> or <c>app.main:app</c> whose module is a file or a package there.
        /// </summary>
        /// <param name="token">One argument, unquoted.</param>
        /// <param name="directory">The folder to resolve it against; never the console's own.</param>
        /// <param name="module">The argument follows <c>-m</c>, so a bare name is a module too.</param>
        public static bool NamesEntryUnder(string token, string directory, bool module)
        {
            // Before anything path-like: a one-letter module such as "m:app" is, to Windows,
            // a path on drive M.
            var reference = ModuleReference.Match(token);
            if (reference.Success || (module && ModuleName.IsMatch(token)))
            {
                string name = reference.Success ? reference.Groups["module"].Value : token;
                string path = Path.Combine(directory, Path.Combine(name.Split('.')));
                return File.Exists(path + ".py")
                    || File.Exists(Path.Combine(path, "__init__.py"))
                    || (module && File.Exists(Path.Combine(path, "__main__.py")));
            }

            return !Path.IsPathRooted(token) && File.Exists(Path.Combine(directory, token));
        }

        /// <summary>
        /// What <paramref name="directory"/> is to Python, and for a folder belonging to a
        /// virtual environment, the project the environment was made in.
        /// </summary>
        public static PythonFolder Classify(string directory, out string? projectRoot)
        {
            projectRoot = null;
            string folder = Path.TrimEndingDirectorySeparator(directory.Trim());
            if (folder.Length == 0 || !Path.IsPathRooted(folder))
            {
                return PythonFolder.None;
            }

            var scripts = ClassifyScripts(folder);
            if (scripts == PythonFolder.VirtualEnvironmentScripts)
            {
                projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(folder));
                return scripts;
            }

            if (scripts != PythonFolder.None)
            {
                return scripts;
            }

            if (File.Exists(Path.Combine(folder, "pyvenv.cfg")))
            {
                projectRoot = Path.GetDirectoryName(folder);
                return PythonFolder.VirtualEnvironment;
            }

            return File.Exists(Path.Combine(folder, "python.exe")) ? PythonFolder.Installation : PythonFolder.None;
        }

        /// <summary>
        /// Whether <paramref name="directory"/> is a <c>Scripts</c> folder of a virtual
        /// environment or of a Python installation. Only a folder called Scripts is looked at
        /// on disk: this runs for every keystroke in the program box.
        /// </summary>
        private static PythonFolder ClassifyScripts(string directory)
        {
            string folder = Path.TrimEndingDirectorySeparator(directory);
            if (!string.Equals(Path.GetFileName(folder), "Scripts", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(folder) is not { } parent)
            {
                return PythonFolder.None;
            }

            if (File.Exists(Path.Combine(parent, "pyvenv.cfg")))
            {
                return PythonFolder.VirtualEnvironmentScripts;
            }

            // An installation, or a conda environment, keeps python.exe above its Scripts
            // folder; a virtual environment that has lost its pyvenv.cfg keeps it inside.
            return File.Exists(Path.Combine(parent, "python.exe")) || File.Exists(Path.Combine(folder, "python.exe"))
                ? PythonFolder.InstallationScripts
                : PythonFolder.None;
        }

        /// <summary>
        /// The PATH a service is started with: the machine's, as services.exe read it at boot.
        /// The user's own — where a per-user Python install and the Store alias live — is not
        /// part of it.
        /// </summary>
        private static string? MachinePath()
        {
            try
            {
                return Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
