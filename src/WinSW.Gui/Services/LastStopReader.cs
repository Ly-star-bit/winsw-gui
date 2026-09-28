using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>What became of one of the log files the Last stop card reads.</summary>
    public enum LogExcerptState
    {
        /// <summary>The file has lines to show: those since the last start, when that is known.</summary>
        Lines,

        /// <summary>Nothing has been written to the file since the service last started.</summary>
        NothingSinceStart,

        /// <summary>The file is there and empty, and when the service last started is not known.</summary>
        Empty,

        /// <summary>No such file where the configuration puts it.</summary>
        Missing,

        /// <summary>The configuration has the wrapper write no such file.</summary>
        NotWritten,

        /// <summary>The file is there and could not be read; see <see cref="LogExcerpt.Error"/>.</summary>
        Unreadable,
    }

    /// <summary>
    /// The end of one log file, as the Last stop card shows it. Immutable: it is read on a worker
    /// and shown on the UI thread.
    /// </summary>
    public sealed class LogExcerpt
    {
        public LogExcerpt(LogExcerptState state, string path, ImmutableArray<string> lines = default, string? error = null)
        {
            this.State = state;
            this.Path = path;
            this.Lines = lines.IsDefault ? ImmutableArray<string>.Empty : lines;
            this.Error = error;
        }

        public LogExcerptState State { get; }

        /// <summary>The file read, or the one looked for when there is none.</summary>
        public string Path { get; }

        public string FileName => System.IO.Path.GetFileName(this.Path);

        public string Directory => System.IO.Path.GetDirectoryName(this.Path) ?? string.Empty;

        /// <summary>The last lines, oldest first; empty unless <see cref="State"/> is <see cref="LogExcerptState.Lines"/>.</summary>
        public ImmutableArray<string> Lines { get; }

        /// <summary>Why the file could not be read, for <see cref="LogExcerptState.Unreadable"/>.</summary>
        public string? Error { get; }

        /// <summary>There is a file to open, whatever was found in it.</summary>
        public bool Exists => this.State is not (LogExcerptState.Missing or LogExcerptState.NotWritten);
    }

    /// <summary>
    /// Why a stopped service stopped, as far as its files and Windows can tell: the end of its error
    /// output and of the wrapper's log since it last started, the service control manager's record
    /// of the stop, and the program's own exit code. Immutable, for the same reason as
    /// <see cref="LogExcerpt"/>. The code Windows recorded for the service is not part of it: the
    /// entry has it at every reading.
    /// </summary>
    public sealed class LastStopReport
    {
        public LastStopReport(
            DateTime? startedAt,
            int? programExitCode,
            LogExcerpt? errorLog,
            LogExcerpt? wrapperLog,
            ServiceEvent? stopEvent,
            bool noConfig = false,
            string? configError = null,
            string? readError = null)
        {
            this.StartedAt = startedAt;
            this.ProgramExitCode = programExitCode;
            this.ErrorLog = errorLog;
            this.WrapperLog = wrapperLog;
            this.EventTime = stopEvent?.Time;
            this.EventId = stopEvent is null ? 0 : LastStopReader.EventNumber(stopEvent.EventId);
            this.EventMessage = stopEvent?.Message.Trim();
            this.NoConfig = noConfig;
            this.ConfigError = configError;
            this.ReadError = readError;
        }

        /// <summary>When the service last started, local time, from the wrapper's log; null when the log does not say.</summary>
        public DateTime? StartedAt { get; }

        /// <summary>
        /// What the program itself exited with, from the wrapper's "Child process … finished with
        /// code N" line. Windows records its own code for the service, which for a program that
        /// ends on its own is 1067 whatever the program said.
        /// </summary>
        public int? ProgramExitCode { get; }

        /// <summary>The program's error output; null when where it goes could not be told.</summary>
        public LogExcerpt? ErrorLog { get; }

        /// <summary>The wrapper's own log; null when where it goes could not be told.</summary>
        public LogExcerpt? WrapperLog { get; }

        /// <summary>When the service control manager recorded the stop, local time; null when it recorded none.</summary>
        public DateTime? EventTime { get; }

        /// <summary>The record's event ID, as Event Viewer shows it: 7031, 7034, 7000, 7009, 7023 or 7024.</summary>
        public int EventId { get; }

        /// <summary>The record's text, in the language Windows is displayed in.</summary>
        public string? EventMessage { get; }

        /// <summary>The service has no configuration file, so where its logs are is not known.</summary>
        public bool NoConfig { get; }

        /// <summary>Why the configuration could not be read, when it could not.</summary>
        public string? ConfigError { get; }

        /// <summary>Why nothing could be read, for a read that failed in a way the reader did not foresee.</summary>
        public string? ReadError { get; }

        /// <summary>
        /// The card's text. <paramref name="format"/> is <see cref="Localization.Localizer.Format"/>
        /// outside tests, which have no dictionaries to read.
        /// </summary>
        public LastStopText Describe(Func<string, object?[], string> format)
        {
            string since = this switch
            {
                { NoConfig: true } => format("M.LastStop.NoConfig", Array.Empty<object?>()),
                { ConfigError: { } error } => format("M.LastStop.ConfigUnreadable", new object?[] { error }),
                { ReadError: { } failure } => format("M.LastStop.CannotRead", new object?[] { failure }),
                { StartedAt: { } started } => format("M.LastStop.Since", new object?[] { started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) }),
                _ => format("M.LastStop.SinceUnknown", Array.Empty<object?>()),
            };

            string eventTitle = this.EventTime is { } time
                ? format("M.LastStop.EventTitle", new object?[] { time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), this.EventId })
                : string.Empty;

            // Nothing found is said only of a search that was made: a read that failed made none.
            string eventText = this switch
            {
                { EventTime: not null } => this.EventMessage ?? string.Empty,
                { ReadError: not null } => string.Empty,
                { StartedAt: null } => format("M.LastStop.NoEventRecent", Array.Empty<object?>()),
                _ => format("M.LastStop.NoEvent", Array.Empty<object?>()),
            };

            var (errorHeading, errorLines, errorNote) = Section(this.ErrorLog, "M.LastStop.ErrorLog");
            var (wrapperHeading, wrapperLines, wrapperNote) = Section(this.WrapperLog, "M.LastStop.WrapperLog");

            return new LastStopText(
                since,
                this.ProgramExitCode is int code ? LastStopReader.ExitCodeText(code) : string.Empty,
                eventTitle,
                eventText,
                errorHeading,
                errorLines,
                errorNote,
                wrapperHeading,
                wrapperLines,
                wrapperNote);

            (string Heading, string Lines, string Note) Section(LogExcerpt? excerpt, string headingKey)
            {
                if (excerpt is null)
                {
                    return (string.Empty, string.Empty, string.Empty);
                }

                string heading = format(headingKey, new object?[] { excerpt.FileName });
                return excerpt.State switch
                {
                    LogExcerptState.Lines => (heading, string.Join(Environment.NewLine, excerpt.Lines), string.Empty),
                    LogExcerptState.NothingSinceStart => (heading, string.Empty, format("M.LastStop.NothingSince", Array.Empty<object?>())),
                    LogExcerptState.Empty => (heading, string.Empty, format("M.LastStop.EmptyFile", Array.Empty<object?>())),
                    LogExcerptState.NotWritten => (heading, string.Empty, format("M.LastStop.NotWritten", Array.Empty<object?>())),
                    LogExcerptState.Unreadable => (heading, string.Empty, format("M.LastStop.CannotRead", new object?[] { excerpt.Error })),
                    _ => (heading, string.Empty, format("M.LastStop.NoFile", new object?[] { excerpt.Directory })),
                };
            }
        }
    }

    /// <summary>
    /// The Last stop card's text, in the interface's language. A section whose heading is empty is
    /// not shown, and neither is an empty line.
    /// </summary>
    public sealed record LastStopText(
        string Since,
        string ProgramExitCode,
        string EventTitle,
        string EventText,
        string ErrorHeading,
        string ErrorLines,
        string ErrorNote,
        string WrapperHeading,
        string WrapperLines,
        string WrapperNote);

    /// <summary>
    /// Reads why a stopped service stopped: see <see cref="LastStopReport"/>. Everything here runs off
    /// the UI thread and touches no entry; the parsing is split from the reading so that it can be
    /// tested without the files and the event log of a Windows machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The files are found the way the Logs page finds them, from the configuration: the log
    /// directory is <c>&lt;logpath&gt;</c> or the configuration's own, the error output is named after
    /// <c>&lt;logname&gt;</c>, and the wrapper's log after the configuration file whatever
    /// <c>&lt;logname&gt;</c> says, which is how the wrapper names it.
    /// </para>
    /// <para>
    /// They are decoded by <see cref="LogTailReader"/> with the encoding chosen on the Logs page, as
    /// the Logs page decodes them: a byte order mark, else UTF-8 when the bytes are valid UTF-8, else
    /// the system's ANSI code page, which on Chinese Windows is GBK and is what a Python or Java
    /// program writes there. Decoding as UTF-8 alone, as the diagnostics bundle does, would turn the
    /// Chinese error message the card is there to show into replacement characters.
    /// </para>
    /// <para>
    /// "Since the last start" is the wrapper's own line for it, "Starting WinSW in service mode",
    /// and not the console's "Starting service", which it also writes to that log when it asks for
    /// a start that may never happen. The program's error output has no time on its lines: all that
    /// can be said of it is whether it has been written to since that start.
    /// </para>
    /// </remarks>
    public static class LastStopReader
    {
        /// <summary>How many lines of each file the card shows.</summary>
        public const int LineCount = 20;

        /// <summary>
        /// How many of the service's events are asked for. Its stop is among the newest: a stopped
        /// service writes nothing after it.
        /// </summary>
        public const int EventsToSearch = 100;

        private const string ScmSource = "Service Control Manager";

        /// <summary>
        /// An event's time is to the second, and the wrapper's line for the start to the
        /// millisecond: a failure within the same second as the start can be dated before it.
        /// </summary>
        private static readonly TimeSpan EventSlack = TimeSpan.FromSeconds(2);

        /// <summary>
        /// The service control manager's records of a service that stopped when nobody asked it to:
        /// 7031 and 7034 terminated unexpectedly, with and without a recovery action to follow;
        /// 7000 failed to start; 7009 did not connect in time; 7023 and 7024 ended with an error,
        /// its own or the service's.
        /// </summary>
        private static readonly ImmutableHashSet<int> StopEventIds = ImmutableHashSet.Create(7000, 7009, 7023, 7024, 7031, 7034);

        /// <summary>
        /// The wrapper's first line in service mode. 3.x says "Starting WinSW in service mode.",
        /// 2.x "Starting WinSW in the service mode", and older still "ServiceWrapper".
        /// </summary>
        private static readonly Regex ServiceStart = new(
            @"Starting (?:WinSW|ServiceWrapper) in (?:the )?service mode",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// The program has exited. On its own: "Child process 'uvicorn (4312)' finished with code 1."
        /// Found exited by a stop: "… finished with code '0'." Under 2.x: "Child process [...]
        /// finished with 1".
        /// </summary>
        private static readonly Regex ChildExit = new(
            @"Child process .*? finished with (?:code )?'?(-?\d+)'?",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads the report for a stopped service. Never throws for a file or a configuration that
        /// cannot be read: what could not be read is said in the report.
        /// </summary>
        /// <param name="configPath">The service's configuration, or null when it has none.</param>
        /// <param name="encoding">How the files are decoded; the Logs page's setting.</param>
        /// <param name="readEvents">
        /// The service's events, newest first: <see cref="EventLogReader.Read"/>, which has already
        /// kept only the records that name this service. Handed in, because the event log is
        /// Windows' alone.
        /// </param>
        public static LastStopReport Read(string? configPath, LogEncodingChoice encoding, Func<IReadOnlyList<ServiceEvent>> readEvents)
        {
            var events = readEvents();
            if (configPath is null)
            {
                return Compose(null, null, events, noConfig: true);
            }

            string logDirectory;
            string stem;
            bool errorNotWritten;
            string? errorPattern;
            try
            {
                var model = ServiceConfigModel.Load(configPath);
                logDirectory = ConfigPaths.ResolveLogDirectory(model, configPath);
                stem = ConfigPaths.ResolveLogBaseName(model, configPath);
                errorNotWritten = model.ErrFileDisabled || string.Equals(model.LogMode, "none", StringComparison.OrdinalIgnoreCase);
                errorPattern = string.IsNullOrWhiteSpace(model.ErrFilePattern) ? null : model.ErrFilePattern!.Trim();
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Compose(null, null, events, configError: e.Message);
            }

            // Named after the configuration file, whatever <logname> says: Program.cs names it from
            // the configuration's base name.
            var wrapper = ReadFile(Path.Combine(logDirectory, Path.GetFileNameWithoutExtension(configPath) + ".wrapper.log"), encoding);

            string defaultErrorLog = Path.Combine(logDirectory, stem + DefaultErrorExtension);
            var error = errorNotWritten
                ? new LogFileRead(defaultErrorLog, LogFileState.NotWritten)
                : ReadFile(FindErrorLog(logDirectory, stem, errorPattern) ?? defaultErrorLog, encoding);

            return Compose(wrapper, error, events);
        }

        /// <summary>The extension every log mode but the rolling ones gives the error output, whatever <c>&lt;errfilepattern&gt;</c> says.</summary>
        internal const string DefaultErrorExtension = ".err.log";

        /// <summary>
        /// Puts together what was read: the wrapper's log decides since when, and the rest is cut to
        /// that. Pure, for the tests.
        /// </summary>
        internal static LastStopReport Compose(
            LogFileRead? wrapper,
            LogFileRead? error,
            IReadOnlyList<ServiceEvent> events,
            bool noConfig = false,
            string? configError = null)
        {
            var wrapperLines = wrapper is { State: LogFileState.Read } ? wrapper.Value.Lines : Array.Empty<string>();
            var (startIndex, startedAt) = FindLastStart(wrapperLines);
            int? programExitCode = FindProgramExitCode(wrapperLines, Math.Max(0, startIndex));

            LogExcerpt? wrapperExcerpt = wrapper is { } w
                ? w.State switch
                {
                    LogFileState.Read => Excerpt(w.Path, wrapperLines.Skip(Math.Max(0, startIndex)).ToList()),
                    _ => Unread(w),
                }
                : null;

            LogExcerpt? errorExcerpt = error is { } e
                ? e.State switch
                {
                    // The program's lines carry no time. Written to before the start, the file holds
                    // nothing of this run; after it, its end is what this run wrote, or at least ends
                    // with it.
                    LogFileState.Read when startedAt is { } start && e.LastWrite < start => new LogExcerpt(LogExcerptState.NothingSinceStart, e.Path),
                    LogFileState.Read when e.Lines.Count == 0 && startedAt != null => new LogExcerpt(LogExcerptState.NothingSinceStart, e.Path),
                    LogFileState.Read => Excerpt(e.Path, e.Lines),
                    _ => Unread(e),
                }
                : null;

            return new LastStopReport(
                startedAt,
                programExitCode,
                errorExcerpt,
                wrapperExcerpt,
                PickStopEvent(events, startedAt),
                noConfig,
                configError);

            static LogExcerpt Excerpt(string path, IReadOnlyList<string> lines) =>
                lines.Count == 0
                    ? new LogExcerpt(LogExcerptState.Empty, path)
                    : new LogExcerpt(LogExcerptState.Lines, path, LastLines(lines, LineCount));

            static LogExcerpt Unread(LogFileRead file) => file.State switch
            {
                LogFileState.NotWritten => new LogExcerpt(LogExcerptState.NotWritten, file.Path),
                LogFileState.Unreadable => new LogExcerpt(LogExcerptState.Unreadable, file.Path, error: file.Error),
                _ => new LogExcerpt(LogExcerptState.Missing, file.Path),
            };
        }

        /// <summary>
        /// Where the last service-mode start is in <paramref name="lines"/>, and when it was; -1 and
        /// null when there is none. The time is null too when the line's own could not be read.
        /// </summary>
        internal static (int Index, DateTime? StartedAt) FindLastStart(IReadOnlyList<string> lines)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (ServiceStart.IsMatch(lines[i]))
                {
                    return (i, TryParseTime(lines[i], out var time) ? time : null);
                }
            }

            return (-1, null);
        }

        /// <summary>The code in the last "Child process … finished with code N" line at or after <paramref name="from"/>, or null.</summary>
        internal static int? FindProgramExitCode(IReadOnlyList<string> lines, int from)
        {
            for (int i = lines.Count - 1; i >= from; i--)
            {
                var match = ChildExit.Match(lines[i]);
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
                {
                    return code;
                }
            }

            return null;
        }

        /// <summary>
        /// The local time a wrapper log line begins with: "2026-09-24T10:15:00.123" from 3.x,
        /// "2026-09-24 10:15:00,123" from 2.x.
        /// </summary>
        internal static bool TryParseTime(string line, out DateTime time)
        {
            time = default;
            const int Length = 23;
            return line.Length >= Length
                && DateTime.TryParseExact(
                    line.AsSpan(0, Length),
                    new[] { "yyyy-MM-dd'T'HH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss,fff" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out time);
        }

        /// <summary>
        /// The newest of <paramref name="events"/> that records the service stopping without being
        /// asked to, and not before <paramref name="since"/>: an older one is about an earlier run.
        /// </summary>
        internal static ServiceEvent? PickStopEvent(IEnumerable<ServiceEvent> events, DateTime? since)
        {
            ServiceEvent? newest = null;
            foreach (var candidate in events)
            {
                if (!IsStopEvent(candidate) || (since is { } start && candidate.Time < start - EventSlack))
                {
                    continue;
                }

                if (newest is null || candidate.Time > newest.Time)
                {
                    newest = candidate;
                }
            }

            return newest;
        }

        /// <summary>A record by the service control manager of a stop nobody asked for.</summary>
        internal static bool IsStopEvent(ServiceEvent candidate) =>
            string.Equals(candidate.Source, ScmSource, StringComparison.OrdinalIgnoreCase)
            && StopEventIds.Contains(EventNumber(candidate.EventId));

        /// <summary>
        /// The event ID as Event Viewer shows it. A record read through EventLogEntry carries its
        /// qualifiers above the low sixteen bits — 7031 comes as 3221232503 — and one already cut
        /// down is left as it is.
        /// </summary>
        internal static int EventNumber(long eventId) => (int)(eventId & 0xFFFF);

        /// <summary>
        /// A file the wrapper writes the program's error output to, for a service whose logs start
        /// with <paramref name="stem"/>: <c>stem.err.log</c> under every log mode, dated
        /// (<c>stem_20260924.err.log</c>) by roll-by-time, numbered (<c>stem.0.err.log</c>,
        /// <c>stem.20260924.#0001.err.log</c>) once rolled by size. Not another service's
        /// <c>stem-worker.err.log</c>, in a log directory two services share.
        /// </summary>
        internal static bool IsErrorLogName(string fileName, string stem, string extension)
        {
            if (!fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                || !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || fileName.Length < stem.Length + extension.Length)
            {
                return false;
            }

            string middle = fileName.Substring(stem.Length, fileName.Length - stem.Length - extension.Length);
            if (middle.Length == 0)
            {
                return true;
            }

            return middle[0] is '.' or '_'
                && middle.Any(char.IsDigit)
                && middle.All(c => char.IsDigit(c) || c is '.' or '_' or '-' or '#');
        }

        /// <summary>
        /// The error output the wrapper wrote to last, in <paramref name="directory"/>; null when
        /// there is none, or the directory cannot be read.
        /// </summary>
        /// <param name="pattern">
        /// <c>&lt;errfilepattern&gt;</c>, which the rolling modes use in place of <c>.err.log</c>.
        /// </param>
        internal static string? FindErrorLog(string directory, string stem, string? pattern)
        {
            try
            {
                var extensions = pattern is null ? new[] { DefaultErrorExtension } : new[] { DefaultErrorExtension, pattern };
                return new DirectoryInfo(directory)
                    .EnumerateFiles(stem + "*")
                    .Where(file => extensions.Any(extension => IsErrorLogName(file.Name, stem, extension)))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Select(file => file.FullName)
                    .FirstOrDefault();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the end of one file: at most the last 128 KB, decoded as the Logs page decodes it,
        /// and the last line whether or not it has its line break yet.
        /// </summary>
        internal static LogFileRead ReadFile(string path, LogEncodingChoice encoding)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new LogFileRead(path, LogFileState.Missing);
                }

                // Opened once first, because the reader takes a file it cannot open for an empty
                // one: said as such, "nothing since the start" would be wrong about a file that
                // holds the answer.
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                }

                var lastWrite = File.GetLastWriteTime(path);
                using var reader = new LogTailReader(path, encoding);
                var lines = reader.ReadNewLines().ToList();
                if (reader.TakePartialLine() is { Length: > 0 } partial)
                {
                    lines.Add(partial.TrimEnd('\r'));
                }

                return new LogFileRead(path, LogFileState.Read, lines, lastWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                return new LogFileRead(path, LogFileState.Unreadable, error: e.Message);
            }
        }

        /// <summary>The last <paramref name="count"/> of <paramref name="lines"/>, oldest first.</summary>
        internal static ImmutableArray<string> LastLines(IReadOnlyList<string> lines, int count) =>
            lines.Skip(Math.Max(0, lines.Count - count)).ToImmutableArray();

        /// <summary>
        /// A program's exit code as the card shows it. A negative one is an NTSTATUS in all but
        /// name — -1073741819 is 0xC0000005, an access violation — and is shown in hex beside it,
        /// the form it is looked up by.
        /// </summary>
        internal static string ExitCodeText(int code) =>
            code < 0
                ? string.Create(CultureInfo.InvariantCulture, $"{code} (0x{code:X8})")
                : code.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Windows' own text for an error code the service control manager recorded, in the language
        /// Windows is displayed in; empty for 0.
        /// </summary>
        public static string SystemText(int code) =>
            code == 0 ? string.Empty : new Win32Exception(code).Message.Trim();

        /// <summary>What reading one of the files came to.</summary>
        internal enum LogFileState
        {
            Read,
            Missing,
            NotWritten,
            Unreadable,
        }

        /// <summary>One file as read, before it is cut to the last start.</summary>
        internal readonly struct LogFileRead
        {
            public LogFileRead(string path, LogFileState state, IReadOnlyList<string>? lines = null, DateTime lastWrite = default, string? error = null)
            {
                this.Path = path;
                this.State = state;
                this.Lines = lines ?? Array.Empty<string>();
                this.LastWrite = lastWrite;
                this.Error = error;
            }

            public string Path { get; }

            public LogFileState State { get; }

            /// <summary>Every line read, oldest first; empty unless <see cref="State"/> is <see cref="LogFileState.Read"/>.</summary>
            public IReadOnlyList<string> Lines { get; }

            /// <summary>When the file was last written to, local time.</summary>
            public DateTime LastWrite { get; }

            public string? Error { get; }
        }
    }
}
