using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinSW.Gui.Services
{
    /// <summary>How the bytes of a log file are turned into text.</summary>
    public enum LogEncodingChoice
    {
        /// <summary>BOM if present; otherwise UTF-8 when the bytes are valid UTF-8, else the system ANSI code page.</summary>
        Auto,
        Utf8,

        /// <summary>The system ANSI code page — GBK on Chinese Windows, Windows-1252 in the West.</summary>
        SystemAnsi,
    }

    /// <summary>How the file under a reader's path was replaced by another since the last read.</summary>
    public enum LogRollover
    {
        None,

        /// <summary>
        /// The old file was read to its end: the lines returned are the last it has. The next
        /// read starts the new file from its beginning.
        /// </summary>
        ReadToEnd,

        /// <summary>
        /// The file was replaced while the reader had let go of it. Whatever the old file
        /// gained after that was not read; it is in the file the old one was renamed to. The
        /// next read starts the new file from its beginning.
        /// </summary>
        WhileReleased,
    }

    /// <summary>The end of a log file as text, read once by <see cref="LogTailReader.ReadTail"/>.</summary>
    public sealed class LogTail
    {
        public LogTail(string text, Encoding? encoding, long skippedBytes)
        {
            this.Text = text;
            this.Encoding = encoding;
            this.SkippedBytes = skippedBytes;
        }

        /// <summary>The text, with the file's own line breaks.</summary>
        public string Text { get; }

        /// <summary>The encoding the text was read in, or null when every byte of it was ASCII.</summary>
        public Encoding? Encoding { get; }

        /// <summary>How much of the file comes before the text and was not read, not counting a byte-order mark.</summary>
        public long SkippedBytes { get; }

        /// <summary>The encoding's name the way the viewer shows it.</summary>
        public string EncodingName => this.Encoding?.WebName.ToUpperInvariant() ?? "ASCII";
    }

    /// <summary>
    /// Incrementally reads a log file that another process is still writing to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is opened with the widest possible share mode so tailing can never block the
    /// service from writing to, rolling, or deleting its own log. Callers pull; there is no
    /// background thread of its own, and nothing here is safe against two calls at once. The
    /// viewer reads on a worker and applies on the UI thread, one read in flight at a time,
    /// and releases the file only while no read is out.
    /// </para>
    /// <para>
    /// The wrapper writes the child's output bytes verbatim, and a console program on Windows
    /// emits the system code page unless it opts into UTF-8. On Chinese Windows that is GBK,
    /// so decoding blindly as UTF-8 turns every Chinese log line into mojibake. Bytes are
    /// therefore buffered up to the last complete line and the encoding is decided from the
    /// first line that contains a non-ASCII byte.
    /// </para>
    /// <para>
    /// The wrapper rolls a log by renaming it (<c>svc.out.log</c> to <c>svc.0.out.log</c>, or
    /// to <c>.old</c> at each start in roll mode) and creating a new file under the old name.
    /// The delete sharing that lets the rename through also means the handle held here follows
    /// the renamed file, which never grows again: the viewer went quiet at the first roll and
    /// said nothing. So whenever a read finds nothing new, the path is asked which file it
    /// names now, and once that is not the file held, the held one is read to its end and let
    /// go, and the path is opened again from its first byte.
    /// </para>
    /// </remarks>
    public sealed class LogTailReader : IDisposable
    {
        /// <summary>How much history to show when a file is opened.</summary>
        private const int InitialTailBytes = 128 * 1024;

        /// <summary>
        /// The most the reader catches up on in one call. Left unbounded, a viewer that had
        /// been paused, or whose page had been out of sight, for an afternoon read the whole
        /// afternoon's output in one go on the UI thread, and then handed every line over to
        /// be appended one notification at a time to a buffer that keeps the last five
        /// thousand. A megabyte is more lines than that buffer holds, so skipping to it loses
        /// nothing the viewer could have shown.
        /// </summary>
        internal const int MaxCatchUpBytes = 1024 * 1024;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

        private readonly string path;
        private readonly LogEncodingChoice choice;
        private readonly byte[] buffer = new byte[64 * 1024];
        private readonly MemoryStream pending = new();

        private FileStream? stream;
        private long position;
        private Encoding? encoding;

        /// <summary>The file the stream is open on, where the file system numbers its files.</summary>
        private FileId? heldId;

        /// <summary>Set by <see cref="Release"/>: the next open carries on at <see cref="position"/>.</summary>
        private bool released;

        /// <summary>Set after a roll: the next open starts at the first byte rather than at the tail.</summary>
        private bool fromStart;

        public LogTailReader(string path, LogEncodingChoice choice = LogEncodingChoice.Auto)
        {
            this.path = path;
            this.choice = choice;
        }

        public string Path => this.path;

        /// <summary>Set when the file shrank since the last read: it was reset in place, and is read again from the start.</summary>
        public bool Restarted { get; private set; }

        /// <summary>
        /// Set when the path came to name a different file since the last read: the wrapper
        /// rolled it. Unlike <see cref="Restarted"/>, what was read before is still true; the
        /// lines returned alongside come before the roll.
        /// </summary>
        public LogRollover Rollover { get; private set; }

        /// <summary>
        /// Bytes passed over unread by the last call, because the file had grown by more than
        /// <see cref="MaxCatchUpBytes"/> since the call before. Zero otherwise.
        /// </summary>
        public long SkippedBytes { get; private set; }

        /// <summary>The encoding in use, or null while auto-detection has only seen ASCII.</summary>
        public Encoding? Encoding => this.encoding;

        public string EncodingName => this.encoding?.WebName.ToUpperInvariant() ?? "ASCII";

        /// <summary>The encoding <see cref="LogEncodingChoice.SystemAnsi"/> resolves to on this machine.</summary>
        public static Encoding SystemAnsiEncoding
        {
            get
            {
                try
                {
                    return System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException)
                {
                    // The code-page provider is not registered; Latin-1 at least never throws.
                    return System.Text.Encoding.Latin1;
                }
            }
        }

        /// <summary>
        /// Returns the complete lines appended since the previous call. The first call returns
        /// the tail of the existing file rather than the whole thing.
        /// </summary>
        public IReadOnlyList<string> ReadNewLines()
        {
            this.Restarted = false;
            this.Rollover = LogRollover.None;
            this.SkippedBytes = 0;
            var lines = new List<string>();

            try
            {
                if (this.stream is null)
                {
                    if (!File.Exists(this.path) || !this.Open(lines))
                    {
                        return lines;
                    }
                }

                // Not FileStream.Length: for a read-only handle .NET may cache it, and this
                // file is being grown by another process the whole time.
                long length = CurrentLength(this.stream!);

                // A file that is still growing is the one being written, so the path is only
                // asked about it when nothing has arrived. After a roll the old file never
                // grows again, so a roll is seen one read after its last line at the latest.
                bool rolled = length == this.position && this.PathNamesAnotherFile(length);
                if (rolled)
                {
                    // The wrapper may have written to it between the measuring and the rename.
                    length = CurrentLength(this.stream!);
                }

                if (length < this.position)
                {
                    // The file shrank: the appender reset it. Start over.
                    this.position = 0;
                    this.pending.SetLength(0);
                    this.Restarted = true;
                    this.DetectFromPreamble();
                }

                if (length - this.position > MaxCatchUpBytes)
                {
                    // More has arrived than is worth reading: the viewer would keep only the
                    // tail of it anyway. Skip to a whole line inside the budget, and drop the
                    // partial line held from before the gap, which no longer joins onto
                    // anything.
                    long resume = this.StartOfNextLine(length - MaxCatchUpBytes);
                    this.SkippedBytes = resume - this.position;
                    this.position = resume;
                    this.pending.SetLength(0);
                }

                if (length > this.position)
                {
                    this.stream!.Position = this.position;

                    int read;
                    while ((read = this.stream.Read(this.buffer, 0, this.buffer.Length)) > 0)
                    {
                        this.pending.Write(this.buffer, 0, read);
                    }

                    this.position = this.stream.Position;
                    this.DrainCompleteLines(lines);
                }

                if (rolled)
                {
                    // Everything the old file will ever hold is read. Let go of it — the wrapper
                    // renames it again at every roll, and on older Windows a file still held
                    // open cannot be deleted from the end of that chain — and take the new
                    // one from its first byte. The encoding stays until the new file decides
                    // its own, so that it still names the one these last lines were read in.
                    this.FlushPartialLine(lines);
                    this.stream!.Dispose();
                    this.stream = null;
                    this.position = 0;
                    this.heldId = null;
                    this.fromStart = true;
                    this.Rollover = LogRollover.ReadToEnd;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file is momentarily locked or was replaced mid-roll; the next tick retries.
                // One that could not be opened at all keeps the place it was to be opened at.
                if (this.stream != null)
                {
                    this.Reset();
                }
            }

            return lines;
        }

        /// <summary>
        /// Closes the file but keeps the place in it, so that a viewer that is not reading
        /// holds nothing open. The next read opens the path again and carries on from that
        /// place, or, when the path names a different file by then, says so through
        /// <see cref="Rollover"/> and starts that file from its beginning. Never while a read
        /// is out.
        /// </summary>
        public void Release()
        {
            if (this.stream is null)
            {
                // Not opened yet, or let go of at a roll: the next open already knows where to start.
                return;
            }

            this.stream.Dispose();
            this.stream = null;
            this.released = true;
        }

        /// <summary>Flushes a trailing line that has no newline yet, so nothing is lost on stop.</summary>
        public string? TakePartialLine()
        {
            if (this.pending.Length == 0)
            {
                return null;
            }

            string value = (this.encoding ?? LenientUtf8).GetString(this.pending.GetBuffer(), 0, (int)this.pending.Length);
            this.pending.SetLength(0);
            return value;
        }

        /// <summary>
        /// Opens the path and decides where reading starts: the tail of the file the first
        /// time, its first byte after a roll, and the place it was let go at after
        /// <see cref="Release"/>. False when there is nothing to read in this call.
        /// </summary>
        private bool Open(List<string> lines)
        {
            this.stream = new FileStream(
                this.path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            FileId? id = Describe(this.stream.SafeFileHandle).Id;

            if (this.released)
            {
                this.released = false;

                if (id is null || this.heldId is null || id == this.heldId)
                {
                    // The same file, or one that cannot be told apart from it: carry on. A file
                    // that was reset meanwhile is shorter than the place, which the read notices.
                    this.heldId ??= id;
                    return true;
                }

                // Rolled while nothing was reading it. What is left of the old file is not to
                // be had through this path; the partial line held from it is all there is.
                this.FlushPartialLine(lines);
                this.heldId = id;
                this.position = 0;
                this.DetectFromPreamble();
                this.Rollover = LogRollover.WhileReleased;
                return false;
            }

            this.heldId = id;

            if (this.fromStart)
            {
                // The file that took over the name after a roll: all of it is new.
                this.fromStart = false;
                this.position = 0;
                this.DetectFromPreamble();
                return true;
            }

            this.position = Math.Max(0, CurrentLength(this.stream) - InitialTailBytes);
            this.DetectFromPreamble();

            if (this.position > 0)
            {
                // Starting mid-file: skip to the next line so the first line shown is whole.
                this.position = this.StartOfNextLine(this.position);
            }

            return true;
        }

        /// <summary>
        /// Whether the path now names a file other than the one held open. No verdict — false —
        /// when the path cannot be asked: it is missing for the moment between the wrapper's
        /// rename and its new file, or locked, or on a share that is not answering. The held
        /// file stays open until the answer is certain, so none of its end is lost.
        /// </summary>
        /// <param name="held">The held file's length, measured just before this call.</param>
        private bool PathNamesAnotherFile(long held)
        {
            try
            {
                using var probe = OpenForQuery(this.path);
                if (probe is null)
                {
                    return false;
                }

                var named = Describe(probe);
                if (named.Id != null && this.heldId != null)
                {
                    return named.Id != this.heldId;
                }

                // No file numbers to compare, so by length, measured so that a write landing
                // in between cannot pass for a roll. The held length came first; the same file
                // measured afterwards can only be as long or longer. When it is longer, the
                // held file is measured again, and the same file is by then at least as long.
                // A new file exactly as long as the old one passes for it until its next
                // write, which is then read from its first byte all the same. Never the
                // creation time: NTFS hands a deleted or renamed file's creation time on to a
                // new file created under its name within fifteen seconds.
                return named.Length < held || (named.Length > held && named.Length > CurrentLength(this.stream!));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// A handle on the path good for asking which file it is and how long, or null when
        /// there is no file there. On Windows the handle asks for attributes only: that is
        /// never refused for sharing, and an on-access virus scanner has no reason to read a
        /// file nobody is reading, which matters for a question asked every few hundred
        /// milliseconds.
        /// </summary>
        private static SafeFileHandle? OpenForQuery(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = Win32.CreateFileW(path, Win32.FILE_READ_ATTRIBUTES, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    return handle;
                }

                // Refused. Usually there is no file there for the moment; but a path longer
                // than MAX_PATH is refused as "not found" too when passed as written, so the
                // answer is left to .NET's own open, which puts such a path to Windows in the
                // form it takes, and says which of the two it was.
                handle.Dispose();
            }

            try
            {
                return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Which file a handle is open on, where that is known, and its current length.</summary>
        private static (FileId? Id, long Length) Describe(SafeFileHandle handle)
        {
            if (OperatingSystem.IsWindows() && Win32.GetFileInformationByHandle(handle, out var info))
            {
                ulong index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                long length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;

                // A file system or redirector with no file numbers reports zero, which proves nothing.
                return (index == 0 ? null : new FileId(info.VolumeSerialNumber, index), length);
            }

            return (null, RandomAccess.GetLength(handle));
        }

        private void DrainCompleteLines(List<string> lines)
        {
            byte[] bytes = this.pending.GetBuffer();
            int count = (int)this.pending.Length;

            int lastNewline = Array.LastIndexOf(bytes, (byte)'\n', count - 1, count);
            if (lastNewline < 0)
            {
                return;
            }

            int complete = lastNewline + 1;

            if (this.encoding is null)
            {
                this.encoding = this.Decide(bytes, complete);
            }

            string text = (this.encoding ?? LenientUtf8).GetString(bytes, 0, complete);

            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                {
                    continue;
                }

                int end = i;
                if (end > start && text[end - 1] == '\r')
                {
                    end--;
                }

                lines.Add(text.Substring(start, end - start));
                start = i + 1;
            }

            // Keep only the incomplete remainder.
            int remainder = count - complete;
            Buffer.BlockCopy(bytes, complete, bytes, 0, remainder);
            this.pending.SetLength(remainder);
        }

        /// <summary>
        /// Hands over a last line that will never get its newline, because the file it is in
        /// has been rolled, decoded the way a complete line would have been.
        /// </summary>
        private void FlushPartialLine(List<string> lines)
        {
            int count = (int)this.pending.Length;
            if (count == 0)
            {
                return;
            }

            byte[] bytes = this.pending.GetBuffer();
            this.encoding ??= this.Decide(bytes, count);

            string line = (this.encoding ?? LenientUtf8).GetString(bytes, 0, count).TrimEnd('\r');
            this.pending.SetLength(0);
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        /// <summary>
        /// Picks an encoding for the complete lines in <paramref name="bytes"/>. Returns null
        /// when nothing decides it yet, i.e. everything so far is plain ASCII.
        /// </summary>
        private Encoding? Decide(byte[] bytes, int count) => Decide(this.choice, bytes.AsSpan(0, count));

        /// <summary>
        /// The rule <see cref="Decide(byte[], int)"/> applies, for whole lines that follow no
        /// byte-order mark: the encoding chosen, or under <see cref="LogEncodingChoice.Auto"/>
        /// UTF-8 when the bytes are valid UTF-8 and the system ANSI code page when they are
        /// not. Null when that leaves it open, because every byte is plain ASCII.
        /// </summary>
        internal static Encoding? Decide(LogEncodingChoice choice, ReadOnlySpan<byte> bytes)
        {
            switch (choice)
            {
                case LogEncodingChoice.Utf8:
                    return LenientUtf8;
                case LogEncodingChoice.SystemAnsi:
                    return SystemAnsiEncoding;
            }

            bool nonAscii = false;
            foreach (byte b in bytes)
            {
                if (b >= 0x80)
                {
                    nonAscii = true;
                    break;
                }
            }

            if (!nonAscii)
            {
                return null;
            }

            try
            {
                StrictUtf8.GetCharCount(bytes);
                return LenientUtf8;
            }
            catch (DecoderFallbackException)
            {
                return SystemAnsiEncoding;
            }
        }

        /// <summary>
        /// The encoding a byte-order mark at the start of a file names, and how many bytes the
        /// mark takes; null and 0 when <paramref name="head"/>, the file's first bytes, starts
        /// with none.
        /// </summary>
        internal static Encoding? FromPreamble(ReadOnlySpan<byte> head, out int length)
        {
            if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                length = 3;
                return LenientUtf8;
            }

            if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                length = 2;
                return System.Text.Encoding.Unicode;
            }

            length = 0;
            return null;
        }

        /// <summary>
        /// The end of a file once, as text: the whole lines among its last
        /// <paramref name="maxBytes"/> bytes, and a last line that has no newline yet, decoded
        /// the way a reader decides for the file on screen. For whoever takes a copy of a log
        /// rather than watching it — the diagnostics bundle, which decoded everything as UTF-8
        /// and so turned the GBK a program writes on Chinese Windows into replacement
        /// characters that nothing could turn back.
        /// </summary>
        /// <remarks>
        /// The encoding is decided on whole lines only. The place the tail is cut at can fall
        /// inside a character, and so can the end of a file still being written; a character
        /// cut in two is not valid UTF-8, and would pass a UTF-8 file off as ANSI.
        /// </remarks>
        public static LogTail ReadTail(string path, long maxBytes, LogEncodingChoice choice = LogEncodingChoice.Auto)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = CurrentLength(stream);

            // A mark is at the start of the file whatever part of it is read, and only an
            // automatic choice goes by it, as a reader's does.
            Span<byte> head = stackalloc byte[3];
            int headLength = stream.Read(head);
            Encoding? marked = null;
            int preamble = 0;
            if (choice == LogEncodingChoice.Auto)
            {
                marked = FromPreamble(head.Slice(0, headLength), out preamble);
            }

            bool wide = marked is UnicodeEncoding;
            int unit = wide ? 2 : 1;
            long start = Math.Max(preamble, length - Math.Max(0, maxBytes));
            if (wide && (start - preamble) % 2 != 0)
            {
                // UTF-16 comes in pairs of bytes counted from the mark.
                start++;
            }

            // Cut short of the start, the text begins at a whole line, as when a reader opens a
            // file at its tail: after the first newline from the character before the cut on,
            // so that a cut that falls just after one loses no line.
            long readFrom = start > preamble ? start - unit : start;
            var bytes = new byte[Math.Max(0, length - readFrom)];
            stream.Position = readFrom;
            int count = 0;
            int read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0)
            {
                count += read;
            }

            int from = 0;
            if (readFrom < start)
            {
                // A tail with no newline in it at all is kept from the cut.
                int newline = NextNewline(bytes, 0, count, wide);
                from = Math.Min(count, newline >= 0 ? newline + unit : unit);
            }

            int lastNewline = LastNewline(bytes, from, count, wide);
            int whole = lastNewline < 0 ? count : lastNewline + unit;
            Encoding? encoding = marked ?? Decide(choice, bytes.AsSpan(from, whole - from));

            return new LogTail((encoding ?? LenientUtf8).GetString(bytes, from, count - from), encoding, readFrom + from - preamble);
        }

        /// <summary>
        /// Where the first newline at or after <paramref name="from"/> is, or -1. In UTF-16 a
        /// newline is the pair 0A 00 at an even offset; in UTF-8, GBK and the other ANSI code
        /// pages the byte 0A is never part of another character.
        /// </summary>
        private static int NextNewline(byte[] bytes, int from, int count, bool wide)
        {
            for (int i = from; i < count; i += wide ? 2 : 1)
            {
                if (bytes[i] == (byte)'\n' && (!wide || (i + 1 < count && bytes[i + 1] == 0)))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Where the last newline between <paramref name="from"/> and <paramref name="count"/> is, or -1.</summary>
        private static int LastNewline(byte[] bytes, int from, int count, bool wide)
        {
            if (!wide)
            {
                return count > from ? Array.LastIndexOf(bytes, (byte)'\n', count - 1, count - from) : -1;
            }

            for (int i = from + ((count - from) / 2 * 2) - 2; i >= from; i -= 2)
            {
                if (bytes[i] == (byte)'\n' && bytes[i + 1] == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static long CurrentLength(FileStream stream) => RandomAccess.GetLength(stream.SafeFileHandle);

        /// <summary>
        /// The offset just past the first newline at or after <paramref name="from"/>, or the
        /// end of the file when there is none: the place a whole line starts.
        /// </summary>
        private long StartOfNextLine(long from)
        {
            this.stream!.Position = from;
            int b;
            while ((b = this.stream.ReadByte()) >= 0 && b != '\n')
            {
            }

            return this.stream.Position;
        }

        private void DetectFromPreamble()
        {
            this.encoding = null;

            if (this.choice != LogEncodingChoice.Auto || this.stream is null || CurrentLength(this.stream) < 2)
            {
                return;
            }

            Span<byte> head = stackalloc byte[3];
            this.stream.Position = 0;
            int read = this.stream.Read(head);

            this.encoding = FromPreamble(head.Slice(0, read), out int preamble);
            this.position = Math.Max(this.position, preamble);
        }

        private void Reset()
        {
            this.stream?.Dispose();
            this.stream = null;
            this.position = 0;
            this.pending.SetLength(0);
            this.encoding = null;
            this.heldId = null;
            this.released = false;
            this.fromStart = false;
        }

        public void Dispose()
        {
            this.Reset();
            this.pending.Dispose();
        }

        /// <summary>
        /// A file as the file system numbers it: the volume's serial number and the file's
        /// index on it. It stays with the file through a rename, and, unlike the creation
        /// time, it is never handed on to a new file created under the old name.
        /// </summary>
        private readonly record struct FileId(uint Volume, ulong Index);

        /// <summary>
        /// The two calls the roll check needs. Kept with the reader rather than with the
        /// service calls in <see cref="NativeMethods"/>: they answer questions about this
        /// reader's file and nothing else.
        /// </summary>
        private static class Win32
        {
            internal const int FILE_READ_ATTRIBUTES = 0x0080;
            internal const int OPEN_EXISTING = 3;

            // FILETIME rather than long for the times: a long would be aligned to eight bytes
            // and push every field after the first four bytes out of place.
            [StructLayout(LayoutKind.Sequential)]
            internal struct BY_HANDLE_FILE_INFORMATION
            {
                public uint FileAttributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
                public uint VolumeSerialNumber;
                public uint FileSizeHigh;
                public uint FileSizeLow;
                public uint NumberOfLinks;
                public uint FileIndexHigh;
                public uint FileIndexLow;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFileW(string fileName, int desiredAccess, FileShare shareMode, IntPtr securityAttributes, int creationDisposition, int flagsAndAttributes, IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);
        }
    }
}
