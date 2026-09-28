using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WinSW.Gui.Model;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a service will actually find, checked before it has to: a program named without a
    /// path looked for the way <c>CreateProcess</c> looks for it in the wrapper, a drive mapped
    /// only at the user's sign-in, a user profile under LocalSystem, a venv whose Python is gone.
    /// </summary>
    /// <remarks>
    /// Every case runs against a machine made up for it, with Windows paths as plain strings, so
    /// the rules are tried the same wherever the tests run. The wording is checked against the
    /// dictionaries as they are in the source, as <see cref="RecoveryPlanTests"/> does.
    /// </remarks>
    public class ServiceEnvironmentTests
    {
        private const string JavaBin = @"C:\Program Files\Java\jdk-21\bin";

        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

        private static readonly Dictionary<string, Dictionary<string, string>> Dictionaries = new(StringComparer.Ordinal);

        // A name alone ---------------------------------------------------------

        [Fact]
        public void ANameInSystem32IsLeftAlone()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Windows\system32\cmd.exe");

            var check = Service("cmd").CheckEnvironment(machine, null);

            Assert.Empty(check.Findings);
            Assert.Null(check.FullExecutablePath);
        }

        [Fact]
        public void ANameBesideTheConfigurationIsLeftAlone()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\app.exe");

            Assert.Empty(Service("app").CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// The wrapper's own folder is searched first, and for an installed service that is the
        /// folder of the wrapper it runs, which need not hold the configuration.
        /// </summary>
        [Fact]
        public void TheInstalledWrappersFolderIsSearchedRatherThanTheConfigurations()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\services\bin\tool.exe");

            Assert.Empty(Service("tool").CheckEnvironment(machine, @"C:\services\bin\WinSW.exe").Findings);
            Assert.Equal(new[] { "M.Warn.NotFoundForService" }, Keys(Service("tool").CheckEnvironment(machine, null)));
        }

        [Fact]
        public void ANameInTheWorkingDirectoryIsLeftAlone()
        {
            var machine = new FakeServiceMachine();
            machine.Directories.Add(@"C:\app");
            machine.Files.Add(@"C:\app\server.exe");
            var model = Service("server");
            model.WorkingDirectory = @"C:\app";

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// The case that passes the try run and later fails or changes: found through the
        /// machine's PATH, which a service takes up only after a restart of Windows.
        /// </summary>
        [Fact]
        public void ANameOnTheMachinePathIsOfferedInFull()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += ";" + JavaBin;
            machine.Files.Add(JavaBin + @"\java.exe");

            var check = Service("java").CheckEnvironment(machine, null);

            var finding = Assert.Single(check.Findings);
            Assert.Equal("M.Warn.OnMachinePath", finding.Key);
            Assert.Equal(new[] { "java", JavaBin + @"\java.exe" }, finding.Values);
            Assert.Equal(JavaBin + @"\java.exe", check.FullExecutablePath);
            Assert.Equal("java", check.Executable);
        }

        /// <summary>PowerShell's folder is on every machine's PATH from the start, and stays there.</summary>
        [Fact]
        public void APathEntryInsideWindowsIsLeftAlone()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");

            var check = Service("powershell").CheckEnvironment(machine, null);

            Assert.Empty(check.Findings);
            Assert.Null(check.FullExecutablePath);
        }

        [Fact]
        public void TheFirstFolderInCreateProcesssOrderWins()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += @";C:\Python312";
            machine.Files.Add(@"C:\Python312\python.exe");
            machine.Files.Add(@"C:\svc\python.exe");

            Assert.Empty(Service("python").CheckEnvironment(machine, null).Findings);
        }

        [Fact]
        public void ANameOnlyOnTheUsersPathIsFlaggedAndOfferedInFull()
        {
            const string UserPython = @"C:\Users\me\AppData\Local\Programs\Python\Python312";
            var machine = new FakeServiceMachine { UserPath = UserPython + @"\;" + UserPython + @"\Scripts\" };
            machine.Files.Add(UserPython + @"\python.exe");

            var check = Service("python").CheckEnvironment(machine, null);

            var finding = Assert.Single(check.Findings);
            Assert.Equal("M.Warn.OnUserPathOnly", finding.Key);
            Assert.Equal(new[] { "python", UserPython + @"\python.exe" }, finding.Values);
            Assert.Equal(UserPython + @"\python.exe", check.FullExecutablePath);
        }

        [Fact]
        public void ANameFoundNowhereIsFlaggedWithNothingToOffer()
        {
            var check = Service("uvicorn").CheckEnvironment(new FakeServiceMachine(), null);

            var finding = Assert.Single(check.Findings);
            Assert.Equal("M.Warn.NotFoundForService", finding.Key);
            Assert.Equal(new[] { "uvicorn" }, finding.Values);
            Assert.Null(check.FullExecutablePath);
        }

        /// <summary>
        /// A file not saved yet has no folder, and the name may be meant for the wrapper's: it
        /// is not called missing, but a PATH it would be found on is still said.
        /// </summary>
        [Fact]
        public void AnUnsavedFileIsNotToldANameIsMissing()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += ";" + JavaBin;
            machine.Files.Add(JavaBin + @"\java.exe");

            Assert.Empty(Service("app", filePath: null).CheckEnvironment(machine, null).Findings);
            Assert.Equal(new[] { "M.Warn.OnMachinePath" }, Keys(Service("java", filePath: null).CheckEnvironment(machine, null)));

            // With the wrapper and the working directory both known, every folder is.
            machine.Directories.Add(@"C:\app");
            var model = Service("app", filePath: null);
            model.WorkingDirectory = @"C:\app";
            Assert.Equal(new[] { "M.Warn.NotFoundForService" }, Keys(model.CheckEnvironment(machine, @"C:\bin\WinSW.exe")));
        }

        /// <summary>Process.Start takes a name in quotes as it is, so the check must too.</summary>
        [Theory]
        [InlineData("\"C:\\Program Files\\App\\app.exe\"")]
        [InlineData("  C:\\Program Files\\App\\app.exe ")]
        [InlineData("\"app\"")]
        public void QuotesAndSpacesAroundTheNameAreNotPartOfIt(string executable)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Program Files\App\app.exe");
            machine.Files.Add(@"C:\svc\app.exe");

            Assert.Empty(Service(executable).CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// .exe is added only to a name with no extension at all. 'python3.11' has one, '.11', so
        /// CreateProcess looks for that file exactly and never for python3.11.exe.
        /// </summary>
        [Theory]
        [InlineData("node", true)]
        [InlineData("node.exe", true)]
        [InlineData("python3.11", false)]
        [InlineData("python3.11.exe", true)]
        public void ExeIsAddedOnlyToANameWithoutAnExtension(string name, bool found)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\node.exe");
            machine.Files.Add(@"C:\svc\python3.11.exe");

            Assert.Equal(found, Service(name).CheckEnvironment(machine, null).Findings.Count == 0);
        }

        /// <summary>npm, yarn and pm2 are batch files, which the command prompt finds by PATHEXT and a service never does.</summary>
        [Fact]
        public void ABatchFileIsNotFoundByItsBareName()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += @";C:\Program Files\nodejs\";
            machine.Files.Add(@"C:\Program Files\nodejs\npm.cmd");

            var check = Service("npm").CheckEnvironment(machine, null);

            var finding = Assert.Single(check.Findings);
            Assert.Equal("M.Warn.ScriptByName", finding.Key);
            Assert.Equal(@"C:\Program Files\nodejs\npm.cmd", check.FullExecutablePath);
        }

        /// <summary>
        /// An &lt;env name="PATH"&gt; replaces the PATH in the wrapper, where it is what
        /// CreateProcess searches: a folder it adds is the configuration's own, and does not move.
        /// </summary>
        [Theory]
        [InlineData(JavaBin + ";%PATH%")]
        [InlineData("%path%;" + JavaBin)]
        [InlineData(JavaBin)]
        public void AFolderTheConfigurationPutsOnThePathIsLeftAlone(string path)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(JavaBin + @"\java.exe");
            var model = Service("java");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "PATH", Value = path });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        [Fact]
        public void TheMachinesPathKeptByTheConfigurationStillMoves()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += ";" + JavaBin;
            machine.Files.Add(JavaBin + @"\java.exe");
            var model = Service("java");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "Path", Value = @"C:\tools;%PATH%" });

            Assert.Equal(new[] { "M.Warn.OnMachinePath" }, Keys(model.CheckEnvironment(machine, null)));
        }

        /// <summary>A configuration that replaces the PATH outright takes the machine's out of the search.</summary>
        [Fact]
        public void APathReplacedOutrightNoLongerHasTheMachines()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += ";" + JavaBin;
            machine.Files.Add(JavaBin + @"\java.exe");
            var model = Service("java");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "PATH", Value = @"C:\tools" });

            Assert.Equal(new[] { "M.Warn.NotFoundForService" }, Keys(model.CheckEnvironment(machine, null)));
        }

        // Variables -----------------------------------------------------------

        /// <summary>
        /// The usual way to name a JDK: an &lt;env&gt; sets JAVA_HOME and the executable is
        /// under it. The wrapper sets its &lt;env&gt; entries in its own process before it expands
        /// the executable, so the program runs, and the check reads it the same way.
        /// </summary>
        [Fact]
        public void AVariableTheConfigurationSetsIsExpandedAsTheWrapperExpandsIt()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\jdk17\bin\java.exe");
            var model = Service(@"%JAVA_HOME%\bin\java");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "JAVA_HOME", Value = @"C:\jdk17" });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);

            // And a program that is not there is named by the path the service would try.
            model.EnvironmentVariables[0].Value = @"C:\jdk21";
            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.ExecutableMissing", finding.Key);
            Assert.Equal(new[] { @"C:\jdk21\bin\java.exe" }, finding.Values);
        }

        /// <summary>
        /// A variable nobody here sets may be set where the service runs, or be a mistake; the
        /// path cannot be told either way, and a warning about a path made up of the %NAME% itself
        /// would be a false one.
        /// </summary>
        [Fact]
        public void APathWithAVariableNobodySetsIsNotChecked()
        {
            var model = Service(@"%APP_HOME%\bin\app");
            model.StopExecutable = @"%APP_HOME%\bin\stop.exe";
            model.WorkingDirectory = @"%APP_HOME%";
            model.LogPath = @"%APP_HOME%\logs";
            model.Prestart.Executable = @"%APP_HOME%\hooks\prep.cmd";

            Assert.Empty(model.CheckEnvironment(new FakeServiceMachine(), null).Findings);
        }

        /// <summary>
        /// The configuration's own variables are what the wrapper sets, taking precedence over
        /// the machine's; a variable the configuration does not set is the machine's.
        /// </summary>
        [Fact]
        public void TheConfigurationsVariableWinsAndTheMachinesFillsIn()
        {
            var machine = new FakeServiceMachine();
            machine.Variables["APP_HOME"] = @"C:\old";
            machine.Variables["ProgramFiles"] = @"C:\Program Files";
            machine.Files.Add(@"C:\new\app.exe");
            machine.Files.Add(@"C:\Program Files\Tool\tool.exe");
            var model = Service(@"%APP_HOME%\app.exe");
            model.StopExecutable = @"%ProgramFiles%\Tool\tool.exe";
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "app_home", Value = @"C:\new" });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// Each &lt;env&gt; is expanded against the ones before it, as the wrapper sets them one by
        /// one; one that names a later entry gets nothing from it.
        /// </summary>
        [Fact]
        public void AVariableSeesOnlyTheEntriesBeforeIt()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\apps\site\run.exe");
            var model = Service(@"%SITE%\run.exe");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "ROOT", Value = @"C:\apps" });
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "SITE", Value = @"%ROOT%\site" });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
            machine.Files.Clear();
            Assert.Equal(new[] { "M.Warn.ExecutableMissing" }, Keys(model.CheckEnvironment(machine, null)));

            // In the other order SITE keeps %ROOT% as it is, and names nothing to look at.
            model.EnvironmentVariables.Move(1, 0);
            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        /// <summary>%BASE% is the configuration's folder and %SERVICE_ID% its id, as the wrapper sets them.</summary>
        [Fact]
        public void TheWrappersOwnVariablesAreItsFolderAndId()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\app\app.exe");
            var model = Service(@"%BASE%\%SERVICE_ID%\%WINSW_SERVICE_ID%.exe");

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);

            // Without a file there is no folder to name, and so nothing to check.
            Assert.Empty(Service(@"%BASE%\missing.exe", filePath: null).CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// A PATH the configuration builds from its own variable finds what is in that folder,
        /// and one built from a variable nobody sets does not make a name missing.
        /// </summary>
        [Fact]
        public void APathBuiltFromTheConfigurationsVariableIsSearched()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\jdk17\bin\java.exe");
            var model = Service("java");
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "JAVA_HOME", Value = @"C:\jdk17" });
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "PATH", Value = @"%JAVA_HOME%\bin;%PATH%" });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);

            model.EnvironmentVariables.RemoveAt(0);
            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        [Theory]
        [InlineData(@"%A%\x", @"C:\a\x", true)]
        [InlineData(@"%a%\%B%", @"C:\a\b", true)]
        [InlineData(@"%NOPE%\x", @"%NOPE%\x", false)]
        [InlineData(@"%PARTIAL%\x", @"%NOPE%\y\x", false)]
        [InlineData("100%", "100%", true)]
        [InlineData("%%", "%%", true)]
        [InlineData("50% %A%", @"50% C:\a", false)]
        [InlineData("%NOPE%A%", "%NOPEC:\\a", false)]
        [InlineData("plain", "plain", true)]
        public void VariablesExpandAsWindowsExpandsThem(string value, string expanded, bool known)
        {
            var variables = new Dictionary<string, (string Value, bool Known)>(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = (@"C:\a", true),
                ["B"] = ("b", true),
                ["PARTIAL"] = (@"%NOPE%\y", false),
            };

            var result = WindowsEnvironment.Expand(value, name => variables.TryGetValue(name, out var found) ? found : null);

            Assert.Equal((expanded, known), result);
        }

        // Paths ---------------------------------------------------------------

        [Theory]
        [InlineData(@"C:\tools\node", true)]
        [InlineData(@"C:\tools\node.exe", true)]
        [InlineData(@"C:\tools\run", false)]
        public void ARootedPathWithoutAnExtensionGetsExe(string executable, bool found)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\tools\node.exe");
            machine.Files.Add(@"C:\tools\run");

            var check = Service(executable).CheckEnvironment(machine, null);

            if (found)
            {
                Assert.Empty(check.Findings);
            }
            else
            {
                var finding = Assert.Single(check.Findings);
                Assert.Equal("M.Warn.ExecutableMissing", finding.Key);
                Assert.Equal(new[] { @"C:\tools\run.exe" }, finding.Values);
            }
        }

        /// <summary>A path with a folder in it is not searched for: it is taken from the working directory.</summary>
        [Fact]
        public void ARelativePathIsTakenFromTheWorkingDirectory()
        {
            var machine = new FakeServiceMachine();
            machine.Directories.Add(@"C:\app");
            machine.Files.Add(@"C:\app\bin\app.exe");
            var model = Service(@"bin\app.exe");
            model.WorkingDirectory = @"C:\app";

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);

            model.Executable = @"lib\app.exe";
            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.ExecutableMissing", finding.Key);
            Assert.Equal(new[] { @"C:\app\lib\app.exe" }, finding.Values);
        }

        /// <summary>
        /// What the check said before it learned anything new: a missing program and a missing
        /// working directory, two warnings, and nothing more about either, even in a user
        /// profile under LocalSystem, since a path that is not there has nothing more to it.
        /// </summary>
        [Fact]
        public void AMissingPathGetsOneWarningOnly()
        {
            var model = Service(@"C:\Users\me\AppData\Local\Temp\x\does-not-exist.exe");
            model.WorkingDirectory = @"C:\Users\me\AppData\Local\Temp\x\nowhere";

            Assert.Equal(
                new[] { "M.Warn.ExecutableMissing", "M.Warn.WorkingDirectoryMissing" },
                Keys(model.CheckEnvironment(new FakeServiceMachine(), null)));
        }

        [Fact]
        public void AStopProgramIsCheckedAsTheProgramIs()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Windows\system32\cmd.exe");
            var model = Service("cmd");
            model.StopExecutable = "stopper";

            Assert.Equal(new[] { "M.Warn.NotFoundForService" }, Keys(model.CheckEnvironment(machine, null)));

            model.StopExecutable = @"C:\app\stop.exe";
            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.StopExecutableMissing", finding.Key);
        }

        /// <summary>Only the executable's own path is offered; a hook's is named in its warning.</summary>
        [Fact]
        public void AHookIsCheckedButNotOffered()
        {
            var machine = new FakeServiceMachine();
            machine.MachinePath += ";" + JavaBin;
            machine.Files.Add(JavaBin + @"\java.exe");
            machine.Files.Add(@"C:\svc\app.exe");
            var model = Service("app");
            model.Prestart.Executable = "java";

            var check = model.CheckEnvironment(machine, null);

            Assert.Equal(new[] { "M.Warn.OnMachinePath" }, Keys(check));
            Assert.Null(check.FullExecutablePath);
        }

        // Network drives --------------------------------------------------------

        [Fact]
        public void AMappedDriveIsFlagged()
        {
            var machine = new FakeServiceMachine();
            machine.NetworkDrives.Add('Z');
            machine.Files.Add(@"z:\apps\app.exe");

            var finding = Assert.Single(Service(@"z:\apps\app.exe").CheckEnvironment(machine, null).Findings);

            Assert.Equal("M.Warn.MappedDrive", finding.Key);
            Assert.Equal(new[] { @"z:\apps\app.exe", "Z:" }, finding.Values);
        }

        /// <summary>The wrapper maps a &lt;sharedDirectoryMapping&gt; drive for the service before it starts anything.</summary>
        [Fact]
        public void ADriveTheServiceMapsItselfIsLeftAlone()
        {
            var machine = new FakeServiceMachine();
            machine.NetworkDrives.Add('Z');
            machine.Files.Add(@"Z:\apps\app.exe");
            var model = Service(@"Z:\apps\app.exe");
            model.SharedDirectories.Add(new DriveMapping { Label = "z:", UncPath = @"\\nas\apps" });

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        [Theory]
        [InlineData(@"\\nas\apps\app.exe", true)]
        [InlineData(@"\\?\UNC\nas\apps\app.exe", true)]
        [InlineData(@"\\?\C:\apps\app.exe", false)]
        public void AShareIsFlagged(string executable, bool share)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(executable);

            Assert.Equal(
                share ? new[] { "M.Warn.NetworkShare" } : Array.Empty<string>(),
                Keys(Service(executable).CheckEnvironment(machine, null)));
        }

        [Fact]
        public void ALogFolderOnAMappedDriveIsFlagged()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\app.exe");
            machine.NetworkDrives.Add('L');
            machine.Directories.Add(@"L:\logs");
            var model = Service("app");
            model.LogPath = @"L:\logs";

            Assert.Equal(new[] { "M.Warn.MappedDrive" }, Keys(model.CheckEnvironment(machine, null)));
        }

        /// <summary>The service control manager cannot even start the wrapper from a drive mapped at sign-in.</summary>
        [Fact]
        public void AWrapperOnAMappedDriveIsFlagged()
        {
            var machine = new FakeServiceMachine();
            machine.NetworkDrives.Add('Z');
            machine.Files.Add(@"C:\Windows\system32\cmd.exe");

            var finding = Assert.Single(Service("cmd", @"Z:\svc\app.xml").CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.WrapperOnMappedDrive", finding.Key);
            Assert.Equal(new[] { @"Z:\svc", "Z:" }, finding.Values);

            Assert.Empty(Service("cmd", @"Z:\svc\app.xml").CheckEnvironment(machine, @"C:\bin\WinSW.exe").Findings);
        }

        // User profiles ---------------------------------------------------------

        [Theory]
        [InlineData(null, "LocalSystem")]
        [InlineData("", "LocalSystem")]
        [InlineData("LocalSystem", "LocalSystem")]
        [InlineData(@"NT AUTHORITY\NetworkService", @"NT AUTHORITY\NetworkService")]
        public void AProgramInAUserProfileIsFlaggedForABuiltInAccount(string? account, string shown)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Users\me\proj\app.exe");
            var model = Service(@"C:\Users\me\proj\app.exe");
            model.ServiceAccountUser = account;

            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);

            Assert.Equal("M.Warn.UserProfile", finding.Key);
            Assert.Equal(new[] { @"C:\Users\me\proj\app.exe", @"C:\Users\me", shown }, finding.Values);
        }

        [Fact]
        public void AProgramInAUserProfileIsLeftAloneForAUserAccount()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\Users\me\proj\app.exe");
            var model = Service(@"C:\Users\me\proj\app.exe");
            model.ServiceAccountUser = @".\me";

            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        [Theory]
        [InlineData(@"C:\Users\Public\app\app.exe")]
        [InlineData(@"C:\Users\Default\app.exe")]
        [InlineData(@"C:\UsersData\me\app.exe")]
        [InlineData(@"D:\Users\me\app.exe")]
        public void ProfilesThatAreNobodysDoNotCount(string executable)
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(executable);

            Assert.Empty(Service(executable).CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// A per-user Python found by name: a service does not have the user's PATH, which is
        /// what the warning says. The profile is a matter for the full path, once it is written.
        /// </summary>
        [Fact]
        public void ANameFoundOnlyInAProfileIsFlaggedOnce()
        {
            var machine = new FakeServiceMachine { UserPath = @"C:\Users\me\AppData\Local\Programs\Python\Python312" };
            machine.Files.Add(@"C:\Users\me\AppData\Local\Programs\Python\Python312\python.exe");

            Assert.Equal(new[] { "M.Warn.OnUserPathOnly" }, Keys(Service("python").CheckEnvironment(machine, null)));
        }

        [Fact]
        public void AWorkingDirectoryInAProfileIsFlaggedAndALogFolderIsNot()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\apps\app.exe");
            machine.Directories.Add(@"C:\Users\me\proj");
            machine.Directories.Add(@"C:\Users\me\logs");
            var model = Service(@"C:\apps\app.exe");
            model.WorkingDirectory = @"C:\Users\me\proj";
            model.LogPath = @"C:\Users\me\logs";

            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.UserProfile", finding.Key);
            Assert.Equal(@"C:\Users\me\proj", finding.Values[0]);
        }

        // Virtual environments ----------------------------------------------------

        [Theory]
        [InlineData(@"C:\apps\site\.venv\Scripts\python.exe")]
        [InlineData(@"C:\apps\site\.venv\Scripts\uvicorn.exe")]
        public void AVenvWhosePythonIsGoneIsFlagged(string executable)
        {
            var machine = Venv(pythonPresent: false);
            machine.Files.Add(executable);

            var finding = Assert.Single(Service(executable).CheckEnvironment(machine, null).Findings);

            Assert.Equal("M.Warn.VenvHomeMissing", finding.Key);
            Assert.Equal(new[] { executable, @"C:\apps\site\.venv", @"C:\Python311" }, finding.Values);
        }

        [Fact]
        public void AVenvWhosePythonIsThereIsLeftAlone()
        {
            var machine = Venv(pythonPresent: true);
            machine.Files.Add(@"C:\apps\site\.venv\Scripts\python.exe");

            Assert.Empty(Service(@"C:\apps\site\.venv\Scripts\python.exe").CheckEnvironment(machine, null).Findings);
        }

        [Fact]
        public void AVenvIsFlaggedOnceHoweverManyProgramsUseIt()
        {
            var machine = Venv(pythonPresent: false);
            machine.Files.Add(@"C:\apps\site\.venv\Scripts\python.exe");
            machine.Files.Add(@"C:\apps\site\.venv\Scripts\alembic.exe");
            var model = Service(@"C:\apps\site\.venv\Scripts\python.exe");
            model.Prestart.Executable = @"C:\apps\site\.venv\Scripts\alembic.exe";

            Assert.Equal(new[] { "M.Warn.VenvHomeMissing" }, Keys(model.CheckEnvironment(machine, null)));
        }

        [Theory]
        [InlineData("home = C:\\Python311\r\ninclude-system-site-packages = false\r\nversion = 3.11.4\r\n", @"C:\Python311")]
        [InlineData("version = 3.12.1\nHome=C:\\Program Files\\Python312\n", @"C:\Program Files\Python312")]
        [InlineData("  home   =   D:\\py  \n", @"D:\py")]
        [InlineData("home =\n", null)]
        [InlineData("homes = C:\\x\n", null)]
        [InlineData("", null)]
        public void PyvenvHomeIsReadAsPythonReadsIt(string text, string? home)
        {
            Assert.Equal(home, ServiceConfigModel.PyvenvHome(text));
        }

        // The account and the rest ------------------------------------------------

        [Fact]
        public void AnUnknownAccountIsFlaggedAndABuiltInOneIsNotLookedUp()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\app.exe");
            machine.UnknownAccounts.Add(@"CORP\nobody");
            machine.UnknownAccounts.Add(@"NT SERVICE\app");
            var model = Service("app");

            model.ServiceAccountUser = @"CORP\nobody";
            Assert.Equal(new[] { "M.Warn.AccountUnknown" }, Keys(model.CheckEnvironment(machine, null)));

            model.ServiceAccountUser = @"NT SERVICE\app";
            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        /// <summary>
        /// A secret "Copy as AI prompt" masked, which the pasted answer kept and nothing could
        /// give back, is named for as long as it is there: the password box shows dots whatever
        /// it holds, and the status line that first said so is gone at the next save.
        /// </summary>
        [Fact]
        public void AMaskLeftFromAPromptIsNamedUntilItIsReplaced()
        {
            var machine = new FakeServiceMachine();
            machine.Files.Add(@"C:\svc\app.exe");
            var model = Service("app");
            model.ServiceAccountUser = @"CORP\svc";
            model.ServiceAccountPassword = WinSW.Gui.Services.ConfigRedactor.Mask;
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "DATABASE_URL", Value = "postgresql://" + WinSW.Gui.Services.ConfigRedactor.Mask + "@db:5432/app" });
            model.EnvironmentVariables.Add(new EnvironmentVariable { Name = "JAVA_HOME", Value = @"C:\jdk" });

            var finding = Assert.Single(model.CheckEnvironment(machine, null).Findings);
            Assert.Equal("M.Warn.MaskedValueLeft", finding.Key);
            Assert.Equal(new[] { "<serviceaccount><password>, <env name=\"DATABASE_URL\">" }, finding.Values);
            Assert.Contains("<env name=\"DATABASE_URL\">", finding.Describe(Text("zh-CN")), StringComparison.Ordinal);

            model.ServiceAccountPassword = "hunter2";
            model.EnvironmentVariables[0].Value = "postgresql://app:pw@db:5432/app";
            Assert.Empty(model.CheckEnvironment(machine, null).Findings);
        }

        // Windows paths as strings --------------------------------------------------

        [Theory]
        [InlineData("java", true)]
        [InlineData("java.exe", true)]
        [InlineData(@"bin\java.exe", false)]
        [InlineData("bin/java.exe", false)]
        [InlineData("C:java.exe", false)]
        [InlineData(@"C:\java.exe", false)]
        public void OnlyANameWithNoFolderIsSearchedFor(string path, bool bare)
        {
            Assert.Equal(bare, WindowsPath.IsBare(path));
        }

        [Theory]
        [InlineData(@"C:\apps\app.exe", @"C:\apps")]
        [InlineData(@"C:\app.exe", @"C:\")]
        [InlineData(@"C:\apps\", "C:\\")]
        [InlineData(@"\\nas\apps\app.exe", @"\\nas\apps")]
        [InlineData(@"C:\", null)]
        [InlineData("app.exe", null)]
        public void TheParentOfAPath(string path, string? parent)
        {
            Assert.Equal(parent, WindowsPath.Parent(path));
        }

        [Theory]
        [InlineData(@"C:\a.b\run", false)]
        [InlineData("run.", false)]
        [InlineData("run.cmd", true)]
        [InlineData(".venv", true)]
        public void AnExtensionIsInTheLastPart(string path, bool extension)
        {
            Assert.Equal(extension, WindowsPath.HasExtension(path));
        }

        [Theory]
        [InlineData(@"C:\Users\me\proj\app.exe", @"C:\Users", @"C:\Users\me")]
        [InlineData(@"c:/users/me/app.exe", @"C:\Users\", @"C:\Users\me")]
        [InlineData(@"C:\Users\me", @"C:\Users", @"C:\Users\me")]
        [InlineData(@"C:\Users", @"C:\Users", null)]
        [InlineData(@"C:\Users\All Users\x", @"C:\Users", null)]
        public void TheProfileAPathIsIn(string path, string profiles, string? profile)
        {
            Assert.Equal(profile, WindowsPath.ProfileFolder(path, profiles));
        }

        [Fact]
        public void APathListIsSplitWithoutItsEmptyEntries()
        {
            Assert.Equal(new[] { @"C:\a", @"C:\b c" }, WindowsPath.SplitList(@";C:\a;; C:\b c ;"));
            Assert.Empty(WindowsPath.SplitList(null));
        }

        // Wording ------------------------------------------------------------------

        /// <summary>
        /// Every message the check can give, in every language: the template takes as many
        /// values as the check passes, and shows each of them.
        /// </summary>
        [Theory]
        [InlineData("M.Warn.ExecutableMissing", 1)]
        [InlineData("M.Warn.StopExecutableMissing", 1)]
        [InlineData("M.Warn.WorkingDirectoryMissing", 1)]
        [InlineData("M.Warn.LogDirectoryMissing", 1)]
        [InlineData("M.Warn.OnMachinePath", 2)]
        [InlineData("M.Warn.OnUserPathOnly", 2)]
        [InlineData("M.Warn.ScriptByName", 2)]
        [InlineData("M.Warn.NotFoundForService", 1)]
        [InlineData("M.Warn.MappedDrive", 2)]
        [InlineData("M.Warn.NetworkShare", 1)]
        [InlineData("M.Warn.WrapperOnMappedDrive", 2)]
        [InlineData("M.Warn.UserProfile", 3)]
        [InlineData("M.Warn.VenvHomeMissing", 3)]
        [InlineData("M.Warn.AccountUnknown", 1)]
        [InlineData("M.Warn.DriverStartMode", 1)]
        [InlineData("M.Warn.ConsolePrompt", 0)]
        [InlineData("M.Warn.MaskedValueLeft", 1)]
        public void EveryMessageShowsWhatItIsGiven(string key, int count)
        {
            string[] values = Enumerable.Range(0, count).Select(i => "«value" + i + "»").ToArray();
            var finding = new EnvironmentFinding(key, values);

            foreach (string code in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                string message = finding.Describe(Text(code));
                Assert.All(values, value => Assert.Contains(value, message, StringComparison.Ordinal));
                Assert.DoesNotContain("{", message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheOfferIsWordedInEveryLanguage()
        {
            foreach (string code in new[] { "en", "zh-CN", "zh-TW", "ja" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Text(code)("M.Editor.FullPathFound")));
                Assert.False(string.IsNullOrWhiteSpace(Text(code)("M.Editor.UseFullPath")));
            }
        }

        [Fact]
        public void TheChineseWarningSaysWhatAnOpsEngineerWouldSay()
        {
            var finding = new EnvironmentFinding("M.Warn.OnUserPathOnly", "python", @"C:\Users\me\py\python.exe");

            Assert.Equal(
                "“python”只能通过你自己的用户 PATH 找到（C:\\Users\\me\\py\\python.exe）。服务只有系统 PATH，没有你的用户 PATH，启动时会报“找不到文件”。请改用完整路径。",
                finding.Describe(Text("zh-CN")));
        }

        private static ServiceConfigModel Service(string executable, string? filePath = @"C:\svc\app.xml")
        {
            var model = ServiceConfigModel.CreateNew();
            model.Id = "app";
            model.Executable = executable;
            model.FilePath = filePath;
            return model;
        }

        private static FakeServiceMachine Venv(bool pythonPresent)
        {
            var machine = new FakeServiceMachine();
            machine.Texts[@"C:\apps\site\.venv\pyvenv.cfg"] = "home = C:\\Python311\r\ninclude-system-site-packages = false\r\nversion = 3.11.4\r\n";
            if (pythonPresent)
            {
                machine.Files.Add(@"C:\Python311\python.exe");
            }

            return machine;
        }

        private static string[] Keys(EnvironmentCheck check) => check.Findings.Select(finding => finding.Key).ToArray();

        private static Func<string, string> Text(string code)
        {
            lock (Dictionaries)
            {
                if (!Dictionaries.TryGetValue(code, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var element in XDocument.Load(Path.Combine(GuiRoot, "Localization", $"Strings.{code}.xaml")).Descendants())
                    {
                        if ((string?)element.Attribute(X + "Key") is { } key)
                        {
                            values[key] = element.Value;
                        }
                    }

                    Dictionaries[code] = values;
                }

                // A missing key throws here rather than coming back as the key, which is what
                // the application would show.
                return key => values[key];
            }
        }

        /// <summary>The WinSW.Gui project directory, found by walking up to the solution.</summary>
        private static string GuiRoot
        {
            get
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory != null && !File.Exists(Path.Combine(directory.FullName, "src", "WinSW.sln")))
                {
                    directory = directory.Parent;
                }

                Assert.True(directory != null, "The repository root could not be found from " + AppContext.BaseDirectory);
                return Path.Combine(directory!.FullName, "src", "WinSW.Gui");
            }
        }
    }

    /// <summary>A machine made up for a test: only the files, folders and drives it is told about exist.</summary>
    internal sealed class FakeServiceMachine : IServiceMachine
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<char> NetworkDrives { get; } = new();

        public HashSet<string> UnknownAccounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The console's own environment variables; none unless a test sets them.</summary>
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What a fresh Windows Server has on its PATH.</summary>
        public string MachinePath { get; set; } =
            @"C:\Windows\system32;C:\Windows;C:\Windows\System32\Wbem;C:\Windows\System32\WindowsPowerShell\v1.0\";

        public string UserPath { get; set; } = string.Empty;

        public string SystemDirectory => @"C:\Windows\system32";

        public string WindowsDirectory => @"C:\Windows";

        public string? ProfilesDirectory { get; set; } = @"C:\Users";

        public bool FileExists(string path) => this.Files.Contains(path);

        public bool DirectoryExists(string path) => this.Directories.Contains(path);

        public string? ReadText(string path) => this.Texts.TryGetValue(path, out string? text) ? text : null;

        public bool IsNetworkDrive(char letter) => this.NetworkDrives.Contains(char.ToUpperInvariant(letter));

        public bool? AccountExists(string account) => !this.UnknownAccounts.Contains(account);

        public string? Variable(string name) => this.Variables.TryGetValue(name, out string? value) ? value : null;
    }
}
