using System;
using System.IO;
using System.Linq;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// How the wizard sets up a Python program: the project a virtual environment belongs to,
    /// the interpreter a picked script runs with, and the folders it must not work in. Each
    /// test runs against a real layout in a temporary folder, the way pip and venv leave one:
    /// <c>api\.venv\pyvenv.cfg</c>, <c>api\.venv\Scripts\uvicorn.exe</c>, <c>api\main.py</c>.
    /// </summary>
    public sealed class PythonProjectTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "winsw-gui-" + Guid.NewGuid().ToString("n"));
        private readonly string project;
        private readonly string environment;

        public PythonProjectTests()
        {
            this.project = Path.Combine(this.directory, "api");
            this.environment = Path.Combine(this.project, ".venv");
            this.Touch("api", ".venv", "pyvenv.cfg");
            this.Touch("api", ".venv", "Scripts", "python.exe");
            this.Touch("api", ".venv", "Scripts", "uvicorn.exe");
            this.Touch("api", "main.py");
            this.Touch("api", "app", "__init__.py");
            this.Touch("api", "app", "server.py");
            this.Touch("api", "jobs", "__main__.py");
            this.Touch("api", "conf", "logging.ini");
            this.Touch("Python311", "python.exe");
            this.Touch("Python311", "Scripts", "uvicorn.exe");
            this.Touch("tools", "Scripts", "cleanup.bat");
            this.Touch("tools", "backup.py");
        }

        private string VenvPython => Path.Combine(this.environment, "Scripts", "python.exe");

        public void Dispose() => Directory.Delete(this.directory, recursive: true);

        /// <summary>
        /// A launcher in a virtual environment's Scripts folder belongs to the project the
        /// environment was made in; one in an installation's Scripts folder, or an interpreter
        /// anywhere, is Python with no project to go on.
        /// </summary>
        [Fact]
        public void ALauncherInAVirtualEnvironmentBelongsToTheFolderAboveIt()
        {
            var uvicorn = PythonProject.Inspect(Path.Combine(this.environment, "Scripts", "uvicorn.exe"), NoPath);
            Assert.True(uvicorn.IsPython);
            Assert.False(uvicorn.IsScript);
            Assert.Equal(this.project, uvicorn.ProjectRoot);

            var installed = PythonProject.Inspect(Path.Combine(this.directory, "Python311", "Scripts", "uvicorn.exe"), NoPath);
            Assert.True(installed.IsPython);
            Assert.Null(installed.ProjectRoot);

            Assert.True(PythonProject.Inspect(Path.Combine(this.directory, "Python311", "python.exe"), NoPath).IsPython);
            Assert.True(PythonProject.Inspect(Path.Combine(this.directory, "Python311", "python3.12.exe"), NoPath).IsPython);
            Assert.True(PythonProject.Inspect("pythonw", NoPath).IsPython);

            // A Scripts folder that has nothing to do with Python.
            Assert.False(PythonProject.Inspect(Path.Combine(this.directory, "tools", "Scripts", "cleanup.bat"), NoPath).IsPython);
            Assert.False(PythonProject.Inspect(Path.Combine(this.directory, "tools", "pythonista.exe"), NoPath).IsPython);
            Assert.Same(PythonTarget.None, PythonProject.Inspect("  ", NoPath));
        }

        /// <summary>
        /// A script is run by the environment nearest it, found from a folder below the
        /// project too; the machine's PATH is not even read then.
        /// </summary>
        [Fact]
        public void AScriptIsRunByTheNearestVirtualEnvironment()
        {
            foreach (string script in new[] { Path.Combine(this.project, "main.py"), Path.Combine(this.project, "app", "server.py") })
            {
                var target = PythonProject.Inspect(script, () => throw new InvalidOperationException("The PATH was read although an environment was there."));

                Assert.True(target.IsPython);
                Assert.True(target.IsScript);
                Assert.Equal(PythonInterpreterSource.VirtualEnvironment, target.InterpreterSource);
                Assert.Equal(this.VenvPython, target.Interpreter);
                Assert.Equal(this.project, target.ProjectRoot);
            }
        }

        /// <summary>
        /// Without an environment, the python.exe on the machine's PATH, written out in full;
        /// never the Store's alias; and when there is none, a bare name the hint warns about.
        /// </summary>
        [Fact]
        public void AScriptWithoutAnEnvironmentFallsBackToThePythonOnThePath()
        {
            string script = Path.Combine(this.directory, "tools", "backup.py");
            string installed = Path.Combine(this.directory, "Python311");
            string store = Path.GetDirectoryName(this.Touch("WindowsApps", "python.exe"))!;

            var found = PythonProject.Inspect(script, () => string.Join(Path.PathSeparator, store, "relative", "\"" + installed + "\""));
            Assert.Equal(PythonInterpreterSource.SystemPath, found.InterpreterSource);
            Assert.Equal(Path.Combine(installed, "python.exe"), found.Interpreter);
            Assert.Null(found.ProjectRoot);

            var missing = PythonProject.Inspect(script, NoPath);
            Assert.Equal(PythonInterpreterSource.NotFound, missing.InterpreterSource);
            Assert.Equal("python", missing.Interpreter);
            Assert.True(missing.IsScript);
        }

        /// <summary>
        /// The script goes ahead of the arguments, quoted; arguments that already name it are
        /// a full Python command line and pass through untouched.
        /// </summary>
        [Fact]
        public void AScriptIsHandedToTheInterpreter()
        {
            string script = Path.Combine(this.project, "main.py");

            Assert.Equal("\"" + script + "\"", PythonProject.ScriptArguments(script, "  "));
            Assert.Equal("\"" + script + "\" --port 8000", PythonProject.ScriptArguments(" " + script + " ", " --port 8000 "));
            Assert.Equal("-X utf8 main.py --port 8000", PythonProject.ScriptArguments(script, "-X utf8 main.py --port 8000"));
        }

        /// <summary>
        /// Relative files and module:app references are looked for in the project, the one
        /// place the service will look for them; a package counts, and after -m a bare module.
        /// </summary>
        [Fact]
        public void EntriesNamedRelativeToTheProjectAreFoundInIt()
        {
            Assert.True(PythonProject.NamesEntryUnder("main.py", this.project, module: false));
            Assert.True(PythonProject.NamesEntryUnder(Path.Combine("app", "server.py"), this.project, module: false));
            Assert.True(PythonProject.NamesEntryUnder("main:app", this.project, module: false));
            Assert.True(PythonProject.NamesEntryUnder("app.server:app", this.project, module: false));
            Assert.True(PythonProject.NamesEntryUnder("app:create_app()", this.project, module: false));
            Assert.True(PythonProject.NamesEntryUnder("jobs", this.project, module: true));

            Assert.False(PythonProject.NamesEntryUnder("jobs", this.project, module: false));
            Assert.False(PythonProject.NamesEntryUnder("uvicorn", this.project, module: true));
            Assert.False(PythonProject.NamesEntryUnder("missing:app", this.project, module: false));
            Assert.False(PythonProject.NamesEntryUnder("8000", this.project, module: false));
        }

        /// <summary>
        /// What the arguments name decides the working directory, and relative to the project
        /// means the project — even with an absolute path to a configuration file after it.
        /// Relative names are never looked up from the console's own current directory.
        /// </summary>
        [Fact]
        public void TheWorkingDirectoryFollowsWhatTheArgumentsNameInTheProject()
        {
            string logConfig = Path.Combine(this.project, "conf", "logging.ini");

            Assert.Equal(this.project, WizardViewModel.ScriptDirectory("main:app --port 8000", this.project));
            Assert.Equal(this.project, WizardViewModel.ScriptDirectory("-m uvicorn main:app", this.project));
            Assert.Equal(this.project, WizardViewModel.ScriptDirectory("-u " + Path.Combine("app", "server.py"), this.project));
            Assert.Equal(this.project, WizardViewModel.ScriptDirectory("-m jobs", this.project));
            Assert.Equal(this.project, WizardViewModel.ScriptDirectory("main:app --log-config \"" + logConfig + "\"", this.project));
            Assert.Null(WizardViewModel.ScriptDirectory("--port 8000", this.project));

            // The script is there relative to this process's current directory, which is not
            // where the service will start. (On another drive there is no relative path.)
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(this.project, "main.py"));
            if (!Path.IsPathRooted(relative))
            {
                Assert.True(File.Exists(relative));
                Assert.Null(WizardViewModel.ScriptDirectory(relative));
            }

            Assert.Null(WizardViewModel.ScriptDirectory("main:app"));
        }

        /// <summary>
        /// The folders a service must not start a Python application in, and for the ones a
        /// virtual environment owns, the project to use instead.
        /// </summary>
        [Fact]
        public void PythonsOwnFoldersAreRecognisedAsWorkingDirectories()
        {
            Assert.Equal(PythonFolder.VirtualEnvironmentScripts, PythonProject.Classify(Path.Combine(this.environment, "Scripts"), out string? root));
            Assert.Equal(this.project, root);

            Assert.Equal(PythonFolder.VirtualEnvironmentScripts, PythonProject.Classify(Path.Combine(this.environment, "Scripts") + Path.DirectorySeparatorChar, out root));
            Assert.Equal(this.project, root);

            Assert.Equal(PythonFolder.VirtualEnvironment, PythonProject.Classify(this.environment, out root));
            Assert.Equal(this.project, root);

            Assert.Equal(PythonFolder.InstallationScripts, PythonProject.Classify(Path.Combine(this.directory, "Python311", "Scripts"), out root));
            Assert.Null(root);

            Assert.Equal(PythonFolder.Installation, PythonProject.Classify(Path.Combine(this.directory, "Python311"), out root));
            Assert.Null(root);

            Assert.Equal(PythonFolder.None, PythonProject.Classify(this.project, out _));
            Assert.Equal(PythonFolder.None, PythonProject.Classify(Path.Combine(this.directory, "tools", "Scripts"), out _));
            Assert.Equal(PythonFolder.None, PythonProject.Classify("relative", out _));
            Assert.Equal(PythonFolder.None, PythonProject.Classify(string.Empty, out _));
        }

        /// <summary>
        /// The editor's Browse, which has only the program to go on: a launcher or the
        /// interpreter in a virtual environment works in the project, never in <c>.venv\Scripts</c>;
        /// anything else, a script included, in its own folder.
        /// </summary>
        [Fact]
        public void APickedProgramWorksInItsProjectOrInItsOwnFolder()
        {
            Assert.Equal(this.project, PythonProject.WorkingDirectoryFor(Path.Combine(this.environment, "Scripts", "uvicorn.exe")));
            Assert.Equal(this.project, PythonProject.WorkingDirectoryFor(this.VenvPython));

            // No project to go on: the folder the program is in, as before.
            Assert.Equal(Path.Combine(this.directory, "Python311"), PythonProject.WorkingDirectoryFor(Path.Combine(this.directory, "Python311", "python.exe")));
            Assert.Equal(Path.Combine(this.directory, "tools", "Scripts"), PythonProject.WorkingDirectoryFor(Path.Combine(this.directory, "tools", "Scripts", "cleanup.bat")));

            // A script below the project works where it is, although the environment that runs it is further up.
            Assert.Equal(Path.Combine(this.project, "app"), PythonProject.WorkingDirectoryFor(Path.Combine(this.project, "app", "server.py")));
        }

        /// <summary>
        /// Picking uvicorn.exe in the project's environment: the project is the working
        /// directory and names the service, and the two Python variables are filled in.
        /// </summary>
        [Fact]
        public void TheWizardSetsUpALauncherFromItsProject()
        {
            var wizard = new WizardViewModel
            {
                TargetPath = Path.Combine(this.environment, "Scripts", "uvicorn.exe"),
                Arguments = "main:app --port 8000",
            };

            Assert.Equal(this.project, wizard.WorkingDirectory);
            Assert.Equal("api", wizard.ServiceId);
            Assert.Equal("api", wizard.DisplayName);
            Assert.True(wizard.TargetIsPython);
            Assert.False(wizard.TargetIsPythonScript);
            Assert.Equal(
                new[] { ("PYTHONUNBUFFERED", "1"), ("PYTHONIOENCODING", "utf-8") },
                wizard.EnvironmentVariables.Select(v => (v.Name, v.Value)));

            var model = wizard.BuildModel();
            Assert.Equal(Path.Combine(this.environment, "Scripts", "uvicorn.exe"), model.Executable);
            Assert.Equal("main:app --port 8000", model.Arguments);
            Assert.Equal(this.project, model.WorkingDirectory);
            Assert.Equal(new[] { "PYTHONUNBUFFERED", "PYTHONIOENCODING" }, model.EnvironmentVariables.Select(v => v.Name));
        }

        /// <summary>
        /// A picked .py is run by the environment's python.exe with the script as its first
        /// argument, in the script's own folder, and named after the project, not "main".
        /// </summary>
        [Fact]
        public void TheWizardRunsAPickedScriptThroughTheEnvironmentsPython()
        {
            string script = Path.Combine(this.project, "app", "server.py");
            var wizard = new WizardViewModel { TargetPath = script, Arguments = "--port 8000" };

            Assert.True(wizard.TargetIsPythonScript);
            Assert.False(wizard.PythonInterpreterMissing);
            Assert.Equal(Path.Combine(this.project, "app"), wizard.WorkingDirectory);
            Assert.Equal("api", wizard.ServiceId);

            var model = wizard.BuildModel();
            Assert.Equal(this.VenvPython, model.Executable);
            Assert.Equal("\"" + script + "\" --port 8000", model.Arguments);
        }

        /// <summary>
        /// The variables are offered once: one the user removed stays removed while they go on
        /// typing, one they edited stays when the program stops being Python, and the untouched
        /// rest goes with it. Suggestions follow the program; what the user typed does not.
        /// </summary>
        [Fact]
        public void OfferedVariablesAndSuggestionsGiveWayToTheUser()
        {
            string other = this.Touch("svc", "server.exe");
            var wizard = new WizardViewModel { TargetPath = Path.Combine(this.environment, "Scripts", "uvicorn.exe") };

            wizard.RemoveEnvironmentVariableCommand.Execute(wizard.EnvironmentVariables.Single(v => v.Name == "PYTHONIOENCODING"));
            wizard.Arguments = "main:app";
            Assert.Equal(new[] { "PYTHONUNBUFFERED" }, wizard.EnvironmentVariables.Select(v => v.Name));

            wizard.EnvironmentVariables.Single().Value = "0";
            wizard.AddEnvironmentVariableCommand.Execute(null);
            wizard.DisplayName = "Orders API";
            wizard.TargetPath = other;

            Assert.Equal(new[] { ("PYTHONUNBUFFERED", "0"), ("NAME", string.Empty) }, wizard.EnvironmentVariables.Select(v => (v.Name, v.Value)));
            Assert.False(wizard.TargetIsPython);
            Assert.Equal("server", wizard.ServiceId);
            Assert.Equal("Orders API", wizard.DisplayName);
            Assert.Equal(Path.GetDirectoryName(other), wizard.WorkingDirectory);

            // Back to Python: a row of that name is already there, whatever its value.
            wizard.TargetPath = Path.Combine(this.environment, "Scripts", "python.exe");
            Assert.Equal(new[] { "PYTHONUNBUFFERED", "NAME", "PYTHONIOENCODING" }, wizard.EnvironmentVariables.Select(v => v.Name));
        }

        private static string? NoPath() => null;

        private string Touch(params string[] parts)
        {
            string path = Path.Combine(this.directory, Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }
    }
}
