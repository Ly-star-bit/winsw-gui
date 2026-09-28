using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// What the line saying why a stop happened is read from: taken from the service or the task
    /// on the UI thread, read on a worker. Plain values, for that reason.
    /// </summary>
    /// <param name="ConfigPath">The configuration, which says where the error output goes; null when there is none.</param>
    /// <param name="Encoding">How the logs are decoded: the Logs page's setting.</param>
    /// <param name="StartedAt">
    /// When the run that ended began, when that is known without the logs: a desktop task's last
    /// run, as the task scheduler has it. Null for a service, whose wrapper records its own start.
    /// </param>
    /// <param name="LastSeenStart">
    /// When the console last saw the service's run begin (<see cref="ServiceEntry.RunStartedAt"/>),
    /// for when the wrapper's log does not say.
    /// </param>
    /// <param name="Stray">
    /// The banner of a program the service's runs left running, or of what holds its port, in
    /// words already; null when there is none.
    /// </param>
    public sealed record StopCauseSource(string? ConfigPath, LogEncodingChoice Encoding, DateTime? StartedAt, DateTime? LastSeenStart, string? Stray);

    /// <summary>The last line a program wrote to its error output, as the chat alert quotes it.</summary>
    /// <param name="FileName">The file it was read from, which the line is introduced by.</param>
    /// <param name="Text">The line, secrets masked and cut to <see cref="StopCause.MaxLength"/>.</param>
    public sealed record ErrorLine(string FileName, string Text);

    /// <summary>
    /// The lines a stop's chat alert gets under it, saying why, when that can be known cheaply: the
    /// last line the program wrote to its error output since the run began, and the program a run
    /// left running or the process holding the port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Stopped unexpectedly (exit code 1067)" is all the group chat used to be told, and 1067 is
    /// what Windows records for any program that ends on its own with a failure. Why is nearly
    /// always the last thing the program wrote to stderr — "No module named 'main'", "address
    /// already in use" — which was two pages away on a console nobody in the chat is looking at.
    /// </para>
    /// <para>
    /// Only a line this run wrote is quoted: one from an earlier run would name a cause that has
    /// already been dealt with. The error output has no times on its lines, so the file is taken
    /// to hold this run's end when it was written to after the run began. When the run began is
    /// the wrapper's own line in its log, as on the Last stop card (<see cref="LastStopReader"/>),
    /// or failing that the start the console saw; a desktop task's is its last run. When none of
    /// them is known, nothing is quoted.
    /// </para>
    /// <para>
    /// The line leaves the machine, so it goes through <see cref="ConfigRedactor.RedactText"/>
    /// first: a connection string in an exception is the usual way a password gets into a log.
    /// It is decoded as the Logs page decodes it, GBK included; see <see cref="LogTailReader.ReadTail"/>.
    /// </para>
    /// <para>
    /// None of this may hold up or lose the alert. The reading is given <see cref="ReadTimeout"/>,
    /// after which the alert goes without it, and anything the reading cannot do leaves the line out.
    /// </para>
    /// </remarks>
    public static class StopCause
    {
        /// <summary>How much of an error line is quoted; a Java stack's first line can run to thousands.</summary>
        public const int MaxLength = 200;

        /// <summary>
        /// How much of the end of each file is read: many times the last line and the wrapper's
        /// last start, which a restarting service writes every few seconds.
        /// </summary>
        internal const long TailBytes = 64 * 1024;

        /// <summary>
        /// How long the alert waits for its cause. The files are local, and read in milliseconds;
        /// a log directory on a share that has gone would hold the message as long as the network
        /// took to give up on it.
        /// </summary>
        internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

        private static readonly char[] LineBreaks = { '\n', '\r' };

        /// <summary>
        /// The notices that say a program stopped when it should not have, and so have a cause to
        /// give: a crash, and a restart loop's count. Not a clean stop, nor a recovery.
        /// </summary>
        public static bool Explains(StopNoticeKind kind) => kind is StopNoticeKind.UnexpectedStop or StopNoticeKind.RepeatedStops;

        /// <summary>
        /// Reads the error line off the calling thread, giving up after <see cref="ReadTimeout"/>;
        /// null when there is none to quote. Never throws.
        /// </summary>
        public static async Task<ErrorLine?> ReadAsync(StopCauseSource source)
        {
            if (source.ConfigPath is null)
            {
                return null;
            }

            var read = Task.Run(() => Read(source));

            // A reading that fails after it has been given up on is still recorded.
            ErrorLog.Observe(read, "alert cause");
            return await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(false) == read && read.IsCompletedSuccessfully
                ? read.Result
                : null;
        }

        /// <summary>
        /// <paramref name="message"/> with the cause under it: the error line, introduced by
        /// <paramref name="lineFormat"/> (<c>M.Alert.Cause</c>: {0} the file, {1} the line), then
        /// the stray banner, each on a line of its own. The message as it is when there is neither.
        /// </summary>
        public static string Append(string message, ErrorLine? line, string lineFormat, string? stray)
        {
            var text = new StringBuilder(message);
            if (line != null)
            {
                text.Append('\n').Append(string.Format(CultureInfo.CurrentCulture, lineFormat, line.FileName, line.Text));
            }

            if (!string.IsNullOrWhiteSpace(stray))
            {
                text.Append('\n').Append(stray.Trim());
            }

            return text.ToString();
        }

        /// <summary>
        /// The last line of the error output written since the run began; null when there is none,
        /// or it cannot be told to be this run's, or the files cannot be read.
        /// </summary>
        internal static ErrorLine? Read(StopCauseSource source)
        {
            if (source.ConfigPath is not { } configPath)
            {
                return null;
            }

            try
            {
                var model = ServiceConfigModel.Load(configPath);
                if (model.ErrFileDisabled || string.Equals(model.LogMode, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                // Found as the Last stop card finds it: the file the wrapper wrote to last, which a
                // rolling mode names with a date or a number.
                string directory = ConfigPaths.ResolveLogDirectory(model, configPath);
                string stem = ConfigPaths.ResolveLogBaseName(model, configPath);
                string? pattern = string.IsNullOrWhiteSpace(model.ErrFilePattern) ? null : model.ErrFilePattern!.Trim();
                if (LastStopReader.FindErrorLog(directory, stem, pattern) is not { } errorLog)
                {
                    return null;
                }

                DateTime? since = source.StartedAt
                    ?? WrapperLogStart(Path.Combine(directory, ConfigPaths.ResolveWrapperLogName(configPath)), source.Encoding)
                    ?? source.LastSeenStart;
                if (since is not { } start || File.GetLastWriteTime(errorLog) < start)
                {
                    return null;
                }

                return LastLine(LogTailReader.ReadTail(errorLog, TailBytes, source.Encoding).Text) is { } line
                    ? new ErrorLine(Path.GetFileName(errorLog), Shorten(ConfigRedactor.RedactText(line)))
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or XmlException or ArgumentException or NotSupportedException or SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// The last line of <paramref name="text"/> with anything on it but white space, trimmed;
        /// null when there is none. A carriage return ends a line as a newline does: a progress
        /// bar redraws itself with them, and only its last state is worth quoting.
        /// </summary>
        internal static string? LastLine(string text)
        {
            int end = text.Length;
            while (end > 0)
            {
                int start = text.LastIndexOfAny(LineBreaks, end - 1) + 1;
                string line = text.Substring(start, end - start).Trim();
                if (line.Length > 0)
                {
                    return line;
                }

                end = start - 1;
            }

            return null;
        }

        /// <summary>
        /// <paramref name="line"/> on one line and no longer than <paramref name="max"/>: tabs and
        /// other control characters become spaces, and a longer line is cut with an ellipsis, never
        /// inside a character that takes two.
        /// </summary>
        internal static string Shorten(string line, int max = MaxLength)
        {
            var text = new StringBuilder(line.Length);
            foreach (char c in line)
            {
                text.Append(char.IsControl(c) ? ' ' : c);
            }

            string flat = text.ToString().Trim();
            if (flat.Length <= max)
            {
                return flat;
            }

            int cut = max - 1;
            if (char.IsHighSurrogate(flat[cut - 1]))
            {
                cut--;
            }

            return flat.Substring(0, cut).TrimEnd() + "…";
        }

        /// <summary>When the wrapper last started in service mode, from the end of its log; null when that cannot be told.</summary>
        private static DateTime? WrapperLogStart(string path, LogEncodingChoice encoding) =>
            File.Exists(path)
                ? LastStopReader.FindLastStart(LogTailReader.ReadTail(path, TailBytes, encoding).Text.Split('\n')).StartedAt
                : null;
    }
}
