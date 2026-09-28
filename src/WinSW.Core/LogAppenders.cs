using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using WinSW.Util;

namespace WinSW
{
    public interface IEventLogger
    {
        void WriteEntry(string message);

        void WriteEntry(string message, EventLogEntryType type);
    }

    internal sealed class TempLogHandler : AbstractFileLogAppender
    {
        private readonly string? outputPath;
        private readonly string? errorPath;

        public TempLogHandler(string? outputPath, string? errorPath)
            : base(string.Empty, string.Empty, IsDisabled(outputPath), IsDisabled(errorPath), string.Empty, string.Empty)
        {
            this.outputPath = outputPath;
            this.errorPath = errorPath;
        }

        private static bool IsDisabled(string? path) => string.IsNullOrEmpty(path) || path!.Equals("NUL", StringComparison.OrdinalIgnoreCase);

        protected override Task LogOutput(StreamReader outputReader)
        {
            return this.CopyStreamAsync(outputReader.BaseStream, new FileStream(this.outputPath!, FileMode.OpenOrCreate));
        }

        protected override Task LogError(StreamReader errorReader)
        {
            return this.CopyStreamAsync(errorReader.BaseStream, new FileStream(this.errorPath!, FileMode.OpenOrCreate));
        }
    }

    /// <summary>
    /// Abstraction for handling log.
    /// </summary>
    public abstract class LogHandler
    {
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.

        protected LogHandler(bool outFileDisabled, bool errFileDisabled)
#pragma warning restore CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.
        {
            this.OutFileDisabled = outFileDisabled;
            this.ErrFileDisabled = errFileDisabled;
        }

        /// <summary>
        /// Error and information about logging should be reported here.
        /// </summary>
        public IEventLogger EventLogger { get; set; }

        public bool OutFileDisabled { get; }

        public bool ErrFileDisabled { get; }

        public abstract void Log(StreamReader outputReader, StreamReader errorReader);

        /// <summary>
        /// Convenience method to copy stuff from StreamReader to StreamWriter
        /// </summary>
        protected async Task CopyStreamAsync(Stream reader, Stream writer)
        {
            var copy = new StreamCopyOperation(reader, writer);
            while (await copy.CopyLineAsync() != 0)
            {
            }

            reader.Dispose();
            writer.Dispose();
        }

        /// <summary>
        /// File replacement.
        /// </summary>
        protected void MoveFile(string sourceFileName, string destFileName)
        {
            try
            {
                FileHelper.MoveOrReplaceFile(sourceFileName, destFileName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Access denied is not an IOException. Older versions of Windows, Windows Server 2016
                // among them, give it when the file being replaced is still held open by another
                // program, such as a log viewer, and thrown from here it would fail the start of a
                // program that is already running.
                this.EventLogger.WriteEntry("Failed to move :" + sourceFileName + " to " + destFileName + " because " + e.Message);
            }
        }
    }

    /// <summary>
    /// Base class for file-based loggers
    /// </summary>
    public abstract class AbstractFileLogAppender : LogHandler
    {
        protected string BaseLogFileName { get; }

        protected string OutFilePattern { get; }

        protected string ErrFilePattern { get; }

        protected AbstractFileLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : base(outFileDisabled, errFileDisabled)
        {
            this.BaseLogFileName = Path.Combine(logDirectory, baseName);
            this.OutFilePattern = outFilePattern;
            this.ErrFilePattern = errFilePattern;
        }

        public override void Log(StreamReader outputReader, StreamReader errorReader)
        {
            if (!this.OutFileDisabled)
            {
                this.SafeLogOutput(outputReader);
            }

            if (!this.ErrFileDisabled)
            {
                this.SafeLogError(errorReader);
            }
        }

        protected abstract Task LogOutput(StreamReader outputReader);

        protected abstract Task LogError(StreamReader errorReader);

        /// <summary>
        /// Opens the log file again after a roll closed it. When it cannot be opened, for example
        /// because another program opened it in the meantime without sharing write access, the
        /// failure is reported and the output is thrown away until the next roll opens it again. An
        /// exception would end the copy instead, and with nothing reading the program's output any
        /// more, the program would hang once the pipe filled up.
        /// </summary>
        protected Stream OpenAfterRoll(string path, FileMode mode)
        {
            try
            {
                return new FileStream(path, mode);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                this.EventLogger.WriteEntry("Failed to open the log file after rolling it, and its output is thrown away until the next roll: " + e.Message);
                return Stream.Null;
            }
        }

        private async void SafeLogOutput(StreamReader outputReader)
        {
            try
            {
                await this.LogOutput(outputReader);
            }
            catch (Exception e)
            {
                this.EventLogger.WriteEntry("Unhandled exception in task. " + e, EventLogEntryType.Error);
            }
        }

        private async void SafeLogError(StreamReader errorReader)
        {
            try
            {
                await this.LogError(errorReader);
            }
            catch (Exception e)
            {
                this.EventLogger.WriteEntry("Unhandled exception in task. " + e, EventLogEntryType.Error);
            }
        }
    }

    public abstract class SimpleLogAppender : AbstractFileLogAppender
    {
        public FileMode FileMode { get; }

        public string OutputLogFileName { get; }

        public string ErrorLogFileName { get; }

        protected SimpleLogAppender(string logDirectory, string baseName, FileMode fileMode, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : base(logDirectory, baseName, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
            this.FileMode = fileMode;
            this.OutputLogFileName = this.BaseLogFileName + ".out.log";
            this.ErrorLogFileName = this.BaseLogFileName + ".err.log";
        }

        protected override Task LogOutput(StreamReader outputReader)
        {
            return this.CopyStreamAsync(outputReader.BaseStream, new FileStream(this.OutputLogFileName, this.FileMode));
        }

        protected override Task LogError(StreamReader errorReader)
        {
            return this.CopyStreamAsync(errorReader.BaseStream, new FileStream(this.ErrorLogFileName, this.FileMode));
        }
    }

    public class DefaultLogAppender : SimpleLogAppender
    {
        public DefaultLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : base(logDirectory, baseName, FileMode.Append, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
        }
    }

    public class ResetLogAppender : SimpleLogAppender
    {
        public ResetLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : base(logDirectory, baseName, FileMode.Create, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
        }
    }

    /// <summary>
    /// LogHandler that throws away output
    /// </summary>
    public class IgnoreLogAppender : LogHandler
    {
        public IgnoreLogAppender()
            : base(true, true)
        {
        }

        public override void Log(StreamReader outputReader, StreamReader errorReader)
        {
        }
    }

    public class TimeBasedRollingLogAppender : AbstractFileLogAppender
    {
        public string Pattern { get; }

        public int Period { get; }

        public int FilesToKeep { get; }

        public TimeBasedRollingLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern, string pattern, int period, int filesToKeep = -1)
            : base(logDirectory, baseName, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
            this.Pattern = pattern;
            this.Period = period;
            this.FilesToKeep = filesToKeep;
        }

        protected override Task LogOutput(StreamReader outputReader)
        {
            return this.CopyStreamWithDateRotationAsync(outputReader, this.OutFilePattern);
        }

        protected override Task LogError(StreamReader errorReader)
        {
            return this.CopyStreamWithDateRotationAsync(errorReader, this.ErrFilePattern);
        }

        /// <summary>
        /// Works like the CopyStream method but does a log rotation based on time.
        /// </summary>
        private async Task CopyStreamWithDateRotationAsync(StreamReader reader, string ext)
        {
            var periodicRollingCalendar = new PeriodicRollingCalendar(this.Pattern, this.Period);
            periodicRollingCalendar.Init();

            Stream writer = new FileStream(this.BaseLogFileName + "_" + periodicRollingCalendar.Format + ext, FileMode.Append);
            var copy = new StreamCopyOperation(reader.BaseStream, writer);
            while (await copy.CopyLineAsync() != 0)
            {
                if (periodicRollingCalendar.ShouldRoll)
                {
                    writer.Dispose();
                    this.PurgeOldFiles(ext);
                    copy.Writer = writer = this.OpenAfterRoll(this.BaseLogFileName + "_" + periodicRollingCalendar.Format + ext, FileMode.Create);
                }
            }

            reader.Dispose();
            writer.Dispose();
        }

        internal void PurgeOldFiles(string ext)
        {
            if (this.FilesToKeep <= 0)
            {
                return;
            }

            var directory = Path.GetDirectoryName(this.BaseLogFileName)!;
            var baseName = Path.GetFileName(this.BaseLogFileName);

            var files = Directory.GetFiles(directory, baseName + "_*" + ext)
                .OrderByDescending(File.GetLastWriteTime)
                .Skip(this.FilesToKeep);

            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A read-only file, or one already marked for deletion while a reader holds it, is
                    // denied rather than in use. This runs in the middle of the copy, which must go on.
                    this.EventLogger.WriteEntry("Failed to purge old log file: " + e.Message);
                }
            }
        }
    }

    public class SizeBasedRollingLogAppender : AbstractFileLogAppender
    {
        public const int BytesPerKB = 1024;
        public const int BytesPerMB = 1024 * BytesPerKB;
        public const int DefaultSizeThreshold = 10 * BytesPerMB; // roll every 10MB.
        public const int DefaultFilesToKeep = 8;

        public int SizeThreshold { get; }

        public int FilesToKeep { get; }

        public SizeBasedRollingLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern, int sizeThreshold, int filesToKeep)
            : base(logDirectory, baseName, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
            this.SizeThreshold = sizeThreshold;
            this.FilesToKeep = filesToKeep;
        }

        public SizeBasedRollingLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : this(logDirectory, baseName, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern, DefaultSizeThreshold, DefaultFilesToKeep)
        {
        }

        protected override Task LogOutput(StreamReader outputReader)
        {
            return this.CopyStreamWithRotationAsync(outputReader, this.OutFilePattern);
        }

        protected override Task LogError(StreamReader errorReader)
        {
            return this.CopyStreamWithRotationAsync(errorReader, this.ErrFilePattern);
        }

        /// <summary>
        /// Works like the CopyStream method but does a log rotation.
        /// </summary>
        private async Task CopyStreamWithRotationAsync(StreamReader reader, string ext)
        {
            Stream writer = new FileStream(this.BaseLogFileName + ext, FileMode.Append);
            var copy = new StreamCopyOperation(reader.BaseStream, writer);
            long fileLength = new FileInfo(this.BaseLogFileName + ext).Length;

            int written;
            while ((written = await copy.CopyLineAsync()) != 0)
            {
                fileLength += written;
                if (fileLength > this.SizeThreshold)
                {
                    writer.Dispose();

                    // Each rolled file is moved on its own, so that one that cannot be moved does not
                    // hold up the rest. A log viewer that follows the file down the rolled names ends
                    // up holding the last one. On older versions of Windows, Windows Server 2016 among
                    // them, deleting it then only marks it for deletion, and the next delete, or a move
                    // onto its name, is denied rather than in use. The file that should have taken its
                    // name is then deleted by the next step instead, as the oldest would have been.
                    for (int j = this.FilesToKeep; j >= 2; j--)
                    {
                        string dst = this.BaseLogFileName + "." + (j - 1) + ext;
                        string src = this.BaseLogFileName + "." + (j - 2) + ext;
                        try
                        {
                            if (File.Exists(dst))
                            {
                                File.Delete(dst);
                            }

                            if (File.Exists(src))
                            {
                                File.Move(src, dst);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            this.EventLogger.WriteEntry("Failed to roll log: " + e.Message);
                        }
                    }

                    bool rolled;
                    try
                    {
                        File.Move(this.BaseLogFileName + ext, this.BaseLogFileName + ".0" + ext);
                        rolled = true;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A reader that does not share delete access, as many log viewers open files,
                        // keeps the file from being renamed.
                        this.EventLogger.WriteEntry("Failed to roll log: " + e.Message);
                        rolled = false;
                    }

                    // A file that could not be rolled is appended to, so that what it holds is kept.
                    // Counting from zero either way, the next try comes after another threshold's
                    // worth of output, not on every line from now on.
                    copy.Writer = writer = this.OpenAfterRoll(this.BaseLogFileName + ext, rolled ? FileMode.Create : FileMode.Append);
                    fileLength = 0;
                }
            }

            reader.Dispose();
            writer.Dispose();
        }
    }

    /// <summary>
    /// Roll log when a service is newly started.
    /// </summary>
    public class RollingLogAppender : SimpleLogAppender
    {
        public RollingLogAppender(string logDirectory, string baseName, bool outFileDisabled, bool errFileDisabled, string outFilePattern, string errFilePattern)
            : base(logDirectory, baseName, FileMode.Append, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
        }

        public override void Log(StreamReader outputReader, StreamReader errorReader)
        {
            if (!this.OutFileDisabled)
            {
                this.MoveFile(this.OutputLogFileName, this.OutputLogFileName + ".old");
            }

            if (!this.ErrFileDisabled)
            {
                this.MoveFile(this.ErrorLogFileName, this.ErrorLogFileName + ".old");
            }

            base.Log(outputReader, errorReader);
        }
    }

    public class RollingSizeTimeLogAppender : AbstractFileLogAppender
    {
        public const int BytesPerKB = 1024;

        public int SizeThreshold { get; }

        public string FilePattern { get; }

        public TimeSpan? AutoRollAtTime { get; }

        /// <summary>
        /// Log files not written to for longer than this many days are zipped. Only the roll at
        /// <see cref="AutoRollAtTime"/> zips files, so without it this has no effect.
        /// </summary>
        public int? ZipOlderThanNumDays { get; }

        /// <summary>
        /// Names the zip files. Like <see cref="ZipOlderThanNumDays"/>, it needs <see cref="AutoRollAtTime"/>.
        /// </summary>
        public string ZipDateFormat { get; }

        public RollingSizeTimeLogAppender(
            string logDirectory,
            string baseName,
            bool outFileDisabled,
            bool errFileDisabled,
            string outFilePattern,
            string errFilePattern,
            int sizeThreshold,
            string filePattern,
            TimeSpan? autoRollAtTime,
            int? zipolderthannumdays,
            string zipdateformat)
            : base(logDirectory, baseName, outFileDisabled, errFileDisabled, outFilePattern, errFilePattern)
        {
            this.SizeThreshold = sizeThreshold;
            this.FilePattern = filePattern;
            this.AutoRollAtTime = autoRollAtTime;
            this.ZipOlderThanNumDays = zipolderthannumdays;
            this.ZipDateFormat = zipdateformat;
        }

        protected override Task LogOutput(StreamReader outputReader)
        {
            return this.CopyStreamWithRotationAsync(outputReader, this.OutFilePattern);
        }

        protected override Task LogError(StreamReader errorReader)
        {
            return this.CopyStreamWithRotationAsync(errorReader, this.ErrFilePattern);
        }

        private async Task CopyStreamWithRotationAsync(StreamReader reader, string extension)
        {
            // The roll at a set time of day runs on a timer thread, while the copy goes on in its own.
            // The lock keeps the two apart: the copy writes under it (see StreamCopyOperation), and a
            // roll, and the end of the copy, close the writer under it.
            object fileLock = new();

            string? baseDirectory = Path.GetDirectoryName(this.BaseLogFileName)!;
            string? baseFileName = Path.GetFileName(this.BaseLogFileName);
            string? logFile = this.BaseLogFileName + extension;

            Stream writer = new FileStream(logFile, FileMode.Append);
            var copy = new StreamCopyOperation(reader.BaseStream, writer, fileLock);
            long fileLength = new FileInfo(logFile).Length;
            bool ended = false;

            // We auto roll at time is configured then we need to create a timer and wait until time is elasped and roll the file over
            System.Timers.Timer? rollTimer = null;
            if (this.AutoRollAtTime is TimeSpan autoRollAtTime)
            {
                // Run at start
                double tickTime = this.SetupRollTimer(autoRollAtTime);
                var timer = rollTimer = new System.Timers.Timer(tickTime);
                timer.Elapsed += (_, _) =>
                {
                    try
                    {
                        timer.Stop();
                        lock (fileLock)
                        {
                            // The timer can still go off once the copy has ended and closed the file,
                            // which is then not opened again.
                            if (ended)
                            {
                                return;
                            }

                            // A file that could not be rolled keeps its length, so that it still rolls
                            // on size when it should.
                            if (Roll(DateTime.Now.AddDays(-1), "Failed to to trigger auto roll at time event due to: "))
                            {
                                fileLength = 0;
                            }
                        }

                        // Next day so check if file can be zipped
                        this.ZipFiles(baseDirectory, extension, baseFileName);
                    }
                    catch (Exception ex)
                    {
                        this.EventLogger.WriteEntry($"Failed to to trigger auto roll at time event due to: {ex.Message}");
                    }
                    finally
                    {
                        // Recalculate the next interval, unless the copy has ended and disposed of the
                        // timer. Under the lock, so that it cannot end in between.
                        lock (fileLock)
                        {
                            if (!ended)
                            {
                                timer.Interval = this.SetupRollTimer(autoRollAtTime);
                                timer.Start();
                            }
                        }
                    }
                };
                timer.Start();
            }

            try
            {
                int written;
                while ((written = await copy.CopyLineAsync()) != 0)
                {
                    lock (fileLock)
                    {
                        fileLength += written;
                        if (fileLength > this.SizeThreshold)
                        {
                            _ = Roll(DateTime.Now, "Failed to roll size time log: ");

                            // Count from zero even when the roll failed and the file is the old one,
                            // or every line after this one would try again and write another event.
                            // The next try comes after another threshold's worth of output.
                            fileLength = 0;
                        }
                    }
                }
            }
            finally
            {
                // Otherwise the timer would go on rolling the file every day after the program has
                // ended, and leave the last file it opened open.
                lock (fileLock)
                {
                    ended = true;
                    rollTimer?.Dispose();
                    writer.Dispose();
                }
            }

            reader.Dispose();

            // Renames the log file to the next number for the date and starts a new one. The writer is
            // closed first, because Windows does not rename a file that is open without sharing delete
            // access. When the rename fails, the failure is reported and the same file is opened again
            // and appended to, so that the copy goes on and no output is lost. Returns whether the file
            // was renamed. Both callers hold the lock, as the copy may be about to write.
            bool Roll(DateTime date, string failureMessage)
            {
                writer.Dispose();

                bool rolled;
                try
                {
                    int nextFileNumber = this.GetNextFileNumber(extension, baseDirectory, baseFileName, date);
                    string? nextFileName = Path.Combine(
                            baseDirectory,
                            string.Format("{0}.{1}.#{2:D4}{3}", baseFileName, date.ToString(this.FilePattern), nextFileNumber, extension));
                    File.Move(logFile, nextFileName);
                    rolled = true;
                }
                catch (Exception e)
                {
                    this.EventLogger.WriteEntry(failureMessage + e.Message);
                    rolled = false;
                }

                copy.Writer = writer = this.OpenAfterRoll(logFile, rolled ? FileMode.Create : FileMode.Append);
                return rolled;
            }
        }

        private void ZipFiles(string directory, string fileExtension, string zipFileBaseName)
        {
            if (this.ZipOlderThanNumDays is null || this.ZipOlderThanNumDays <= 0)
            {
                return;
            }

            try
            {
                foreach (string path in Directory.GetFiles(directory, "*" + fileExtension))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-this.ZipOlderThanNumDays.Value))
                    {
                        continue;
                    }

                    string sourceFileName = Path.GetFileName(path);
                    string zipFilePattern = fileInfo.LastAccessTimeUtc.ToString(this.ZipDateFormat);
                    string zipFilePath = Path.Combine(directory, $"{zipFileBaseName}.{zipFilePattern}.zip");
                    this.ZipOneFile(path, sourceFileName, zipFilePath);

                    File.Delete(path);
                }
            }
            catch (Exception e)
            {
                this.EventLogger.WriteEntry($"Failed to Zip files. Error {e.Message}");
            }
        }

        private void ZipOneFile(string sourceFilePath, string entryName, string zipFilePath)
        {
            ZipArchive? zipArchive = null;
            try
            {
                zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Update);

                if (zipArchive.GetEntry(entryName) is null)
                {
                    zipArchive.CreateEntryFromFile(sourceFilePath, entryName);
                }
            }
            catch (Exception e)
            {
                this.EventLogger.WriteEntry($"Failed to Zip the File {sourceFilePath}. Error {e.Message}");
            }
            finally
            {
                zipArchive?.Dispose();
            }
        }

        private double SetupRollTimer(TimeSpan autoRollAtTime)
        {
            var nowTime = DateTime.Now;
            var scheduledTime = new DateTime(
                nowTime.Year,
                nowTime.Month,
                nowTime.Day,
                autoRollAtTime.Hours,
                autoRollAtTime.Minutes,
                autoRollAtTime.Seconds,
                0);
            if (nowTime > scheduledTime)
            {
                scheduledTime = scheduledTime.AddDays(1);
            }

            double tickTime = (scheduledTime - DateTime.Now).TotalMilliseconds;
            return tickTime;
        }

        private int GetNextFileNumber(string ext, string baseDirectory, string baseFileName, DateTime now)
        {
            int nextFileNumber = 0;
            string[]? files = Directory.GetFiles(baseDirectory, string.Format("{0}.{1}.#*{2}", baseFileName, now.ToString(this.FilePattern), ext));
            if (files.Length == 0)
            {
                nextFileNumber = 1;
            }
            else
            {
                foreach (string? f in files)
                {
                    try
                    {
                        string? filenameOnly = Path.GetFileNameWithoutExtension(f);
                        int hashIndex = filenameOnly.IndexOf('#');
                        string? lastNumberAsString = filenameOnly.Substring(hashIndex + 1, 4);
                        if (int.TryParse(lastNumberAsString, out int lastNumber))
                        {
                            if (lastNumber > nextFileNumber)
                            {
                                nextFileNumber = lastNumber;
                            }
                        }
                        else
                        {
                            throw new IOException($"File {f} does not follow the pattern provided");
                        }
                    }
                    catch (Exception e)
                    {
                        throw new IOException($"Failed to process file {f} due to error {e.Message}", e);
                    }
                }

                if (nextFileNumber == 0)
                {
                    throw new IOException("Cannot roll the file because matching pattern not found");
                }

                nextFileNumber++;
            }

            return nextFileNumber;
        }
    }

    internal sealed class StreamCopyOperation
    {
        private const int BufferSize = 1024;

        private readonly byte[] buffer;
        private readonly Stream reader;

        // Held over each write. Code on another thread that replaces the writer, such as a roll at a
        // set time of day, closes it and puts in the next one under the same lock.
        private readonly object writerLock;

        private int startIndex;
        private int endIndex;

        internal Stream Writer;

        internal StreamCopyOperation(Stream reader, Stream writer, object? writerLock = null)
        {
            this.buffer = new byte[BufferSize];
            this.reader = reader;
            this.writerLock = writerLock ?? new object();
            this.startIndex = 0;
            this.endIndex = 0;
            this.Writer = writer;
        }

        internal async Task<int> CopyLineAsync()
        {
            byte[] buffer = this.buffer;
            var source = this.reader;
            int startIndex = this.startIndex;
            int endIndex = this.endIndex;

            int total = 0;
            while (true)
            {
                if (startIndex == 0)
                {
                    if ((endIndex = await source.ReadAsync(buffer, 0, BufferSize)) == 0)
                    {
                        break;
                    }
                }

                int buffered = endIndex - startIndex;

                int newLineIndex = Array.IndexOf(buffer, (byte)'\n', startIndex, buffered);
                if (newLineIndex >= 0)
                {
                    int count = newLineIndex - startIndex + 1;
                    total += count;
                    this.Write(buffer, startIndex, count);
                    startIndex = (newLineIndex + 1) % BufferSize;
                    break;
                }

                total += buffered;
                this.Write(buffer, startIndex, buffered);
                startIndex = 0;
            }

            this.startIndex = startIndex;
            this.endIndex = endIndex;

            return total;
        }

        // The writer is looked up at each write, not once for the line: the copy spends most of its
        // time waiting for the program's output, and a roll on another thread in the meantime closes
        // the writer and puts another in its place. Holding the lock keeps the roll from doing that
        // in the middle of a write.
        private void Write(byte[] buffer, int offset, int count)
        {
            lock (this.writerLock)
            {
                this.Writer.Write(buffer, offset, count);
                this.Writer.Flush();
            }
        }
    }
}