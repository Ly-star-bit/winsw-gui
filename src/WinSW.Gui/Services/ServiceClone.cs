using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// An installed service's configuration made ready to start another service from: the
    /// whole file, so that everything the wizard has no field for comes along — the account,
    /// the stop settings, hooks, dependencies, every recovery action — with the paths that led
    /// back to the source's own folder written out in full.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>%BASE%</c> is the folder holding the configuration, and the copy lives in a folder
    /// of its own. Left as it is, a <c>%BASE%</c> that found the program, its arguments or a
    /// hook would look for them in the new, empty folder, and the copy would fail at its first
    /// start. The same goes for a configuration with no working directory, which runs in its
    /// own folder: the copy is given the source's folder outright.
    /// </para>
    /// <para>
    /// What the service writes keeps <c>%BASE%</c> — the log directory, a hook's captured
    /// output, a download's target — so that the copy writes into its own folder rather than
    /// over the source's files.
    /// </para>
    /// </remarks>
    public sealed class ServiceClone
    {
        /// <summary>How long the wrapper waits without a failure before starting its actions over, when the file does not say.</summary>
        public const string WrapperResetPeriod = "1 day";

        private const string WorkingDirectoryLabel = "<workingdirectory>";

        /// <summary>
        /// <c>--port 8000</c>, <c>--port=8000</c>, <c>--http-port 81</c>, <c>-Dserver.port=8080</c>:
        /// an option whose name ends in "port", as its own word, followed by a number. What comes
        /// before "port" in the name is kept, for <see cref="ClientPortName"/>.
        /// </summary>
        private static readonly Regex PortOption = new(
            @"(?<![\w-])-{1,2}(?<name>(?:[\w.-]*[._-])?)port(?=[=:\s])[=:\s]\s*[""']?(?<port>\d{1,5})(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// <c>DB_PORT</c>, <c>REDIS_PORT</c>, <c>--db-port</c>, <c>-Dspring.redis.port</c>: a port
        /// named after something the program connects to, which it does not listen on. A word of
        /// the name that starts with one of these is enough; a Java system property's <c>D</c>
        /// comes off the first word.
        /// </summary>
        /// <remarks>
        /// A list of what to leave out rather than of what to keep: a port the program listens on
        /// is called anything — <c>PORT</c>, <c>HTTP_PORT</c>, <c>--admin-port</c>,
        /// <c>METRICS_PORT</c> — while the ones it connects to are named after a few kinds of
        /// server. Counted as its own, <c>DB_PORT=5432</c> on a machine that runs PostgreSQL had
        /// the preflight name postgres.exe as in the way, and offer to end it.
        /// </remarks>
        private static readonly Regex ClientPortName = new(
            @"(?:^(?-i:D)?|[._-])(?:db|database|redis|mysql|postgres|pg|mongo|smtp|mail|amqp|rabbit|broker|cache|proxy|remote|upstream|backend)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary><c>-p 3000</c>, the short form many servers take.</summary>
        private static readonly Regex ShortPortOption = new(
            @"(?<![\w-])-p[=\s]\s*(?<port>\d{2,5})(?!\d)",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// <c>-b 0.0.0.0:5000</c>, <c>--bind=127.0.0.1:9000</c>, <c>--listen=*:8080</c>,
        /// <c>--urls http://*:5000</c>: an address to listen on, whose ports are read off it.
        /// </summary>
        private static readonly Regex BindOption = new(
            @"(?<![\w-])(?:--bind|-b|--listen|--urls|--address|--addr)[=\s]\s*[""']?(?<value>[^\s""']+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex AddressPort = new(@":(?<port>\d{2,5})(?!\d)", RegexOptions.CultureInvariant);

        /// <summary>
        /// <c>0.0.0.0:8000</c>, <c>http://*:5000</c>, <c>:8000</c> standing on their own. Only a
        /// host that means "every address" counts: <c>localhost:6379</c> in a Redis URL is a
        /// port the program connects to, not one it listens on.
        /// </summary>
        private static readonly Regex ListenAddress = new(
            @"(?<=^|[\s""'=])(?:https?://)?(?:0\.0\.0\.0|\*|\+|\[::\])?:(?<port>\d{2,5})(?=$|[\s""'/])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary><c>PORT</c>, <c>HTTP_PORT</c>, <c>APP_PORT</c>: a variable that is a port by name.</summary>
        private static readonly Regex PortVariable = new(@"(?:^|[._])PORT$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly string xml;

        /// <summary>The settings whose <c>%BASE%</c> was written out, by the label <see cref="Inputs"/> gives them.</summary>
        private readonly HashSet<string> rebased;

        private readonly bool sourceLogs;
        private readonly string sourceLogDirectory;
        private readonly string sourceLogBaseName;

        private ServiceClone(string sourceName, string sourceDirectory, ServiceConfigModel model, HashSet<string> rebased, string logDirectory, string logBaseName)
        {
            this.SourceName = sourceName;
            this.SourceDirectory = sourceDirectory;
            this.rebased = rebased;
            this.sourceLogs = !string.Equals(model.LogMode, "none", StringComparison.OrdinalIgnoreCase);
            this.sourceLogDirectory = logDirectory;
            this.sourceLogBaseName = logBaseName;
            this.LogMode = model.LogMode;
            this.RollPattern = model.RollPattern;
            this.Recovery = model.FailureActions.Select(a => (a.Action, a.Delay)).ToArray();
            this.ResetFailureAfter = model.ResetFailureAfter;
            this.Ports = FindPorts(string.Join(" ", model.Arguments, model.StartArguments), model.EnvironmentVariables);
            this.xml = model.ToXmlString();
        }

        /// <summary>The service the copy is taken from.</summary>
        public string SourceName { get; }

        /// <summary>The folder the source's configuration sits in, which its <c>%BASE%</c> meant.</summary>
        public string SourceDirectory { get; }

        /// <summary>The source's log mode, which may be one the wizard does not offer.</summary>
        public string LogMode { get; }

        /// <summary>The source's roll-by-time pattern, or null.</summary>
        public string? RollPattern { get; }

        /// <summary>The source's <c>&lt;onfailure&gt;</c> rows, in order.</summary>
        public IReadOnlyList<(string Action, string? Delay)> Recovery { get; }

        /// <summary>True when one of <see cref="Recovery"/> does something when the program fails.</summary>
        public bool HasRecovery => this.Recovery.Any(a => !string.Equals(a.Action, "none", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The row the wizard shows the copied recovery by, in its one "restart after" field: the
        /// first restart, or with no restart among the rows, the first that does something. Null
        /// when <see cref="HasRecovery"/> is false.
        /// </summary>
        /// <remarks>
        /// Not simply the first row. A <c>none</c> ahead of a restart waits for nothing, and the
        /// field would show a restart at once that the file never asks for.
        /// </remarks>
        public (string Action, string? Delay)? ShownRecovery
        {
            get
            {
                (string Action, string? Delay)? first = null;
                foreach (var row in this.Recovery)
                {
                    if (row.Action == "restart")
                    {
                        return row;
                    }

                    if (first is null && !string.Equals(row.Action, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        first = row;
                    }
                }

                return first;
            }
        }

        /// <summary>The source's <c>&lt;resetfailure&gt;</c>, or null for the wrapper's own <see cref="WrapperResetPeriod"/>.</summary>
        public string? ResetFailureAfter { get; }

        /// <summary>The ports the source's arguments and environment say it listens on; see <see cref="FindPorts"/>.</summary>
        public IReadOnlyList<int> Ports { get; }

        /// <summary>
        /// Reads the source's configuration and rewrites what referred to its folder.
        /// </summary>
        /// <exception cref="IOException">The file cannot be read.</exception>
        /// <exception cref="InvalidDataException">The file is not a WinSW configuration.</exception>
        /// <exception cref="UnauthorizedAccessException">The file may not be read.</exception>
        public static ServiceClone Load(string sourceName, string configPath)
        {
            var model = ServiceConfigModel.Load(configPath);
            string directory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;

            // Where the source's logs go, read before anything moves, so that a copy about to
            // write into the very same files can be told apart from one that merely shares a
            // folder.
            string logDirectory = ConfigPaths.ResolveLogDirectory(model, configPath);
            string logBaseName = ConfigPaths.ResolveLogBaseName(model, configPath);

            var rebased = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (label, value, set) in Inputs(model))
            {
                if (value is not null && value.Contains("%BASE%", StringComparison.OrdinalIgnoreCase))
                {
                    set(Rebase(value, directory));
                    rebased.Add(label);
                }
            }

            if (string.IsNullOrWhiteSpace(model.WorkingDirectory))
            {
                // The wrapper runs a program without a working directory in the folder the
                // configuration sits in, which for the copy would be its own empty one; a
                // program there finds none of the files its relative paths name.
                model.WorkingDirectory = directory;
                rebased.Add(WorkingDirectoryLabel);

                // A relative path the service writes to was relative to that same folder. The
                // working directory now names the source's, so it is anchored to %BASE%
                // instead, which keeps it in the copy's own folder where it was.
                model.LogPath = AnchorToBase(model.LogPath);
                foreach (var hook in new[] { model.Prestart, model.Poststart, model.Prestop, model.Poststop })
                {
                    hook.StdoutPath = AnchorToBase(hook.StdoutPath);
                    hook.StderrPath = AnchorToBase(hook.StderrPath);
                }

                foreach (var download in model.Downloads)
                {
                    download.To = AnchorToBase(download.To) ?? download.To;
                }
            }

            return new ServiceClone(sourceName, directory, model, rebased, logDirectory, logBaseName);
        }

        /// <summary>
        /// A model of the copy for the wizard to fill in. Each call parses anew: the wizard
        /// builds its model once for the preview and again to install, and rows it adds to
        /// one must not be there waiting in the next.
        /// </summary>
        /// <remarks>
        /// The copy is a new service. The source's rows taken out — recovery unticked, or a
        /// source whose only row said <c>none</c> — leave Windows nothing to clear, and are
        /// written as no <c>&lt;onfailure&gt;</c> at all, as for any new service; see
        /// <see cref="ServiceConfigModel.DeclaredFailureActions"/>.
        /// </remarks>
        public ServiceConfigModel NewModel()
        {
            var model = ServiceConfigModel.FromXml(this.xml, null);
            model.ForgetDeclaredFailureActions();
            return model;
        }

        /// <summary>
        /// The settings of <paramref name="model"/> that were rewritten to name the source's
        /// folder and still do, as XML element names for the review step to list.
        /// </summary>
        public IReadOnlyList<string> PointingIntoSource(ServiceConfigModel model) =>
            Inputs(model)
                .Where(input => this.rebased.Contains(input.Label)
                    && input.Value?.Contains(this.SourceDirectory, StringComparison.OrdinalIgnoreCase) == true)
                .Select(input => input.Label)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// True when <paramref name="model"/>, written to <paramref name="configPath"/>, would
        /// write its logs into the very files the source writes: the same directory and the
        /// same <c>&lt;logname&gt;</c>. Two wrappers cannot both hold one log file open.
        /// </summary>
        public bool SharesLogFilesWith(ServiceConfigModel model, string configPath)
        {
            if (!this.sourceLogs
                || configPath.Length == 0
                || string.Equals(model.LogMode, "none", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                return string.Equals(ConfigPaths.ResolveLogBaseName(model, configPath), this.sourceLogBaseName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(Normalize(ConfigPaths.ResolveLogDirectory(model, configPath)), Normalize(this.sourceLogDirectory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }

            static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        /// <summary>
        /// True when <paramref name="model"/> runs as a named account with its password
        /// written in the file. A copy carries the password as it was when the source was
        /// set up; an account whose password has changed since fails every start with 1069.
        /// </summary>
        /// <remarks>
        /// The accounts Windows manages itself have no password to go stale: the built-in
        /// ones, and a group managed service account, whose name ends in <c>$</c>.
        /// </remarks>
        public static bool CarriesPassword(ServiceConfigModel model)
        {
            string user = model.ServiceAccountUser?.Trim() ?? string.Empty;
            return user.Length > 0
                && !string.IsNullOrWhiteSpace(model.ServiceAccountPassword)
                && !user.EndsWith('$')
                && !user.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
                && !user.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)
                && !user.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The ports a command line and a set of environment variables say the program listens
        /// on, in the order they appear: <c>--port 8000</c> and its spellings, <c>-p 3000</c>,
        /// a listening address such as <c>-b 0.0.0.0:5000</c>, and a variable such as
        /// <c>PORT=8000</c>.
        /// </summary>
        /// <remarks>
        /// A reading of what is written, not of what the program does: a port given only by
        /// position, or in a file the program reads, is not found. It is enough to say that a
        /// copy with the source's arguments will want the source's port. A port named for what
        /// the program connects to, such as <c>DB_PORT</c>, is not one it listens on and is left
        /// out; see <see cref="ClientPortName"/>.
        /// </remarks>
        public static IReadOnlyList<int> FindPorts(string? arguments, IEnumerable<EnvironmentVariable> environment)
        {
            var ports = new List<int>();

            Read(arguments);
            foreach (var variable in environment)
            {
                string name = variable.Name.Trim();
                if (PortVariable.IsMatch(name) && !ClientPortName.IsMatch(name))
                {
                    Add(variable.Value.Trim());
                }

                // A variable can hold a command line of its own (GUNICORN_CMD_ARGS) or an
                // address to listen on (ASPNETCORE_URLS, BIND).
                Read(variable.Value);
            }

            return ports;

            void Read(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                foreach (Match match in PortOption.Matches(text))
                {
                    if (!ClientPortName.IsMatch(match.Groups["name"].Value))
                    {
                        Add(match.Groups["port"].Value);
                    }
                }

                foreach (Match match in ShortPortOption.Matches(text))
                {
                    Add(match.Groups["port"].Value);
                }

                foreach (Match match in BindOption.Matches(text))
                {
                    foreach (Match address in AddressPort.Matches(match.Groups["value"].Value))
                    {
                        Add(address.Groups["port"].Value);
                    }
                }

                foreach (Match match in ListenAddress.Matches(text))
                {
                    Add(match.Groups["port"].Value);
                }
            }

            void Add(string value)
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
                    && port is > 0 and <= 65535
                    && !ports.Contains(port))
                {
                    ports.Add(port);
                }
            }
        }

        /// <summary>
        /// Replaces <c>%BASE%</c>, in any case, with <paramref name="directory"/>. Nothing else
        /// is expanded: a variable such as <c>%ProgramData%</c> means the same to the copy, and
        /// <c>%SERVICE_ID%</c> is meant to follow the service it is in.
        /// </summary>
        internal static string Rebase(string value, string directory) =>
            value.Replace("%BASE%", Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase);

        /// <summary>A relative path made relative to <c>%BASE%</c>; anything else as it is.</summary>
        internal static string? AnchorToBase(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            string trimmed = path.Trim();

            // Rooted in the Windows sense whatever this runs on — a drive, a share, a leading
            // backslash — or starting with a variable, whose value is anyone's guess.
            bool rooted = Path.IsPathRooted(trimmed)
                || (trimmed.Length >= 2 && trimmed[1] == ':')
                || trimmed.StartsWith('\\')
                || trimmed.StartsWith('%');
            return rooted ? path : @"%BASE%\" + trimmed;
        }

        /// <summary>
        /// The settings that name what the service reads or runs, with a label for each — its
        /// XML element — and a way to change it. What it writes is not among them.
        /// </summary>
        private static IEnumerable<(string Label, string? Value, Action<string> Set)> Inputs(ServiceConfigModel model)
        {
            yield return ("<executable>", model.Executable, value => model.Executable = value);
            yield return ("<arguments>", model.Arguments, value => model.Arguments = value);
            yield return ("<startarguments>", model.StartArguments, value => model.StartArguments = value);
            yield return ("<stopexecutable>", model.StopExecutable, value => model.StopExecutable = value);
            yield return ("<stoparguments>", model.StopArguments, value => model.StopArguments = value);
            yield return (WorkingDirectoryLabel, model.WorkingDirectory, value => model.WorkingDirectory = value);

            foreach (var (name, hook) in new[] { ("prestart", model.Prestart), ("poststart", model.Poststart), ("prestop", model.Prestop), ("poststop", model.Poststop) })
            {
                yield return ($"<{name}>", hook.Executable, value => hook.Executable = value);
                yield return ($"<{name}>", hook.Arguments, value => hook.Arguments = value);
            }

            foreach (var variable in model.EnvironmentVariables)
            {
                yield return ($"<env name=\"{variable.Name}\">", variable.Value, value => variable.Value = value);
            }
        }
    }
}
