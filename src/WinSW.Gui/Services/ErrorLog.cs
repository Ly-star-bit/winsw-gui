using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// A record of what went wrong inside this console itself: the failures that reach the
    /// "unexpected error" dialog, the one that ends the process, and the background tasks that
    /// failed with nobody waiting for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, such a failure left nothing on disk. A dialog was all there was, and for a
    /// background task not even that: it went to <c>Debug.WriteLine</c>, which a Release build
    /// compiles away. When the console misbehaves on a colleague's server, this file is the
    /// thing to ask for.
    /// </para>
    /// <para>
    /// Beside <see cref="ActionLog"/> in <c>%LOCALAPPDATA%\WinSW.Gui</c>, and kept the same way:
    /// past <see cref="ActionLog.MaxBytes"/> the file is moved aside to <c>errors.1.log</c> and a
    /// new one begun. Each entry is one tab-separated header line — time, where the failure was
    /// caught, the console's version, Windows, .NET, and whether the console ran as
    /// administrator — then the exception as .NET prints it, inner exceptions and stack traces
    /// included, then a blank line. In English whatever the interface language, like the action
    /// log, so that a file from one machine reads the same as a file from another.
    /// </para>
    /// </remarks>
    public static class ErrorLog
    {
        /// <summary>
        /// What the header says about this process. None of it changes while the process runs,
        /// and the fatal path is no place to go asking the operating system again.
        /// </summary>
        private static readonly Lazy<string> ProcessDescription = new(DescribeProcess);

        public static string FilePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinSW.Gui",
            "errors.log");

        /// <summary>
        /// Records one failure. Never throws: this is called from the handlers of last resort,
        /// where an exception of its own would replace the one being recorded.
        /// </summary>
        /// <param name="source">Where it was caught, e.g. "UI thread" or "update check".</param>
        public static void Record(string source, Exception? exception) =>
            ActionLog.Append(FilePath, Format(DateTime.Now, source, ProcessDescription.Value, exception));

        /// <summary>
        /// Records the failure of a task nobody awaits.
        /// </summary>
        /// <remarks>
        /// Discarding the task (<c>_ = RefreshAsync()</c>) leaves its failure to
        /// <see cref="TaskScheduler.UnobservedTaskException"/>, which is raised only when the
        /// garbage collector finalizes the task — late, and in a console that sits in the tray
        /// for weeks, possibly never. This records it when it happens.
        /// </remarks>
        /// <param name="what">What the task was doing, e.g. "update check".</param>
        public static void Observe(Task task, string what) => _ = Observe(task, what, Record);

        /// <summary>
        /// The same, recording through <paramref name="record"/> and returning the continuation,
        /// so that a test can wait for it without writing to the user's own log.
        /// </summary>
        internal static Task Observe(Task task, string what, Action<string, Exception?> record) =>
            task.ContinueWith(
                t => record(what, t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        /// <summary>
        /// Opens the log in whatever opens .log files, or its folder when nothing has gone
        /// wrong yet.
        /// </summary>
        public static void Open() => ActionLog.OpenLogFile(FilePath);

        /// <summary>
        /// One entry: the header line, the exception, and a line break, so that with the line
        /// break <see cref="ActionLog.Append"/> adds, entries are separated by a blank line.
        /// </summary>
        /// <remarks>
        /// A faulted task's exception is an <see cref="AggregateException"/> that may hold
        /// others of the same kind; flattened, every failure inside it is printed at one level
        /// rather than as a nest of wrappers.
        /// </remarks>
        internal static string Format(DateTime at, string source, string process, Exception? exception)
        {
            string header = string.Join(
                "\t",
                at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                source,
                process);

            string detail = exception switch
            {
                null => "No exception object was given.",
                AggregateException aggregate => aggregate.Flatten().ToString(),
                _ => exception.ToString(),
            };

            return header + Environment.NewLine + detail + Environment.NewLine;
        }

        /// <summary>
        /// The console's full version, commit included, since a report is only as good as the
        /// ability to find the code that produced it; then Windows, .NET and the rights held.
        /// </summary>
        private static string DescribeProcess() => string.Join(
            "\t",
            "WinSW GUI " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? UpdateChecker.CurrentGuiVersion),
            Environment.OSVersion.VersionString + " " + RuntimeInformation.OSArchitecture,
            RuntimeInformation.FrameworkDescription + " " + RuntimeInformation.ProcessArchitecture,
            Elevation.IsElevated ? "administrator" : "standard user");
    }
}
