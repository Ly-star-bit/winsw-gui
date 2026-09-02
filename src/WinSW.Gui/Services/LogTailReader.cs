using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

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

    /// <summary>
    /// Incrementally reads a log file that another process is still writing to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is opened with the widest possible share mode so tailing can never block the
    /// service from writing to, rolling, or deleting its own log. Callers pull; there is no
    /// background thread.
    /// </para>
    /// <para>
    /// The wrapper writes the child's output bytes verbatim, and a console program on Windows
    /// emits the system code page unless it opts into UTF-8. On Chinese Windows that is GBK,
    /// so decoding blindly as UTF-8 turns every Chinese log line into mojibake. Bytes are
    /// therefore buffered up to the last complete line and the encoding is decided from the
    /// first line that contains a non-ASCII byte.
    /// </para>
    /// </remarks>
    public sealed class LogTailReader : IDisposable
    {
        /// <summary>How much history to show when a file is opened.</summary>
        private const int InitialTailBytes = 128 * 1024;

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);

        private readonly string path;
        private readonly LogEncodingChoice choice;
        private readonly byte[] buffer = new byte[64 * 1024];
        private readonly MemoryStream pending = new();

        private FileStream? stream;
        private long position;
        private Encoding? encoding;

        public LogTailReader(string path, LogEncodingChoice choice = LogEncodingChoice.Auto)
        {
            this.path = path;
            this.choice = choice;
        }

        public string Path => this.path;

        /// <summary>Set when the file was rolled or truncated since the last read.</summary>
        public bool Restarted { get; private set; }

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
            var lines = new List<string>();

            try
            {
                if (this.stream is null)
                {
                    if (!File.Exists(this.path))
                    {
                        return lines;
                    }

                    this.stream = new FileStream(
                        this.path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete,
                        bufferSize: 4096,
                        FileOptions.SequentialScan);

                    this.position = Math.Max(0, CurrentLength(this.stream) - InitialTailBytes);
                    this.DetectFromPreamble();

                    if (this.position > 0)
                    {
                        // Starting mid-file: skip to the next line so the first line shown is whole.
                        this.stream.Position = this.position;
                        int b;
                        while ((b = this.stream.ReadByte()) >= 0 && b != '\n')
                        {
                        }

                        this.position = this.stream.Position;
                    }
                }

                // Not FileStream.Length: for a read-only handle .NET may cache it, and this
                // file is being grown by another process the whole time.
                long length = CurrentLength(this.stream);

                if (length < this.position)
                {
                    // The file shrank: the appender reset or rolled it. Start over.
                    this.position = 0;
                    this.pending.SetLength(0);
                    this.Restarted = true;
                    this.DetectFromPreamble();
                }

                if (length == this.position)
                {
                    return lines;
                }

                this.stream.Position = this.position;

                int read;
                while ((read = this.stream.Read(this.buffer, 0, this.buffer.Length)) > 0)
                {
                    this.pending.Write(this.buffer, 0, read);
                }

                this.position = this.stream.Position;
                this.DrainCompleteLines(lines);
            }
            catch (IOException)
            {
                // The file is momentarily locked or was replaced mid-roll; the next tick retries.
                this.Reset();
            }
            catch (UnauthorizedAccessException)
            {
                this.Reset();
            }

            return lines;
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
        /// Picks an encoding for the complete lines in <paramref name="bytes"/>. Returns null
        /// when nothing decides it yet, i.e. everything so far is plain ASCII.
        /// </summary>
        private Encoding? Decide(byte[] bytes, int count)
        {
            switch (this.choice)
            {
                case LogEncodingChoice.Utf8:
                    return LenientUtf8;
                case LogEncodingChoice.SystemAnsi:
                    return SystemAnsiEncoding;
            }

            bool nonAscii = false;
            for (int i = 0; i < count; i++)
            {
                if (bytes[i] >= 0x80)
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
                StrictUtf8.GetCharCount(bytes, 0, count);
                return LenientUtf8;
            }
            catch (DecoderFallbackException)
            {
                return SystemAnsiEncoding;
            }
        }

        private static long CurrentLength(FileStream stream) => RandomAccess.GetLength(stream.SafeFileHandle);

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

            if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                this.encoding = LenientUtf8;
                this.position = Math.Max(this.position, 3);
            }
            else if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                this.encoding = System.Text.Encoding.Unicode;
                this.position = Math.Max(this.position, 2);
            }
        }

        private void Reset()
        {
            this.stream?.Dispose();
            this.stream = null;
            this.position = 0;
            this.pending.SetLength(0);
            this.encoding = null;
        }

        public void Dispose()
        {
            this.Reset();
            this.pending.Dispose();
        }
    }
}
