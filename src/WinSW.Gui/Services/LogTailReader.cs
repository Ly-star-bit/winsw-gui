using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Incrementally reads a log file that another process is still writing to.
    /// </summary>
    /// <remarks>
    /// The file is opened with the widest possible share mode so tailing can never block the
    /// service from writing to, rolling, or deleting its own log. Callers pull; there is no
    /// background thread, which keeps everything on the UI thread's timer and out of trouble.
    /// </remarks>
    public sealed class LogTailReader : IDisposable
    {
        /// <summary>How much history to show when a file is opened.</summary>
        private const int InitialTailBytes = 128 * 1024;

        private readonly string path;
        private readonly Decoder decoder;
        private readonly byte[] buffer = new byte[64 * 1024];
        private readonly char[] chars;

        private FileStream? stream;
        private long position;
        private string partialLine = string.Empty;

        public LogTailReader(string path)
        {
            this.path = path;
            var encoding = new UTF8Encoding(false, false);
            this.decoder = encoding.GetDecoder();
            this.chars = new char[encoding.GetMaxCharCount(this.buffer.Length)];
        }

        public string Path => this.path;

        /// <summary>Set when the file was rolled or truncated since the last read.</summary>
        public bool Restarted { get; private set; }

        /// <summary>
        /// Returns the lines appended since the previous call. The first call returns the
        /// tail of the existing file rather than the whole thing.
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

                    this.position = Math.Max(0, this.stream.Length - InitialTailBytes);
                }

                long length = this.stream.Length;

                if (length < this.position)
                {
                    // The file shrank: the appender reset or rolled it. Start over.
                    this.position = 0;
                    this.partialLine = string.Empty;
                    this.decoder.Reset();
                    this.Restarted = true;
                }

                if (length == this.position)
                {
                    return lines;
                }

                this.stream.Position = this.position;

                int read;
                var text = new StringBuilder(this.partialLine);
                this.partialLine = string.Empty;

                while ((read = this.stream.Read(this.buffer, 0, this.buffer.Length)) > 0)
                {
                    int decoded = this.decoder.GetChars(this.buffer, 0, read, this.chars, 0);
                    text.Append(this.chars, 0, decoded);
                }

                this.position = this.stream.Position;
                SplitLines(text.ToString(), lines, out this.partialLine);
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
            if (this.partialLine.Length == 0)
            {
                return null;
            }

            string value = this.partialLine;
            this.partialLine = string.Empty;
            return value;
        }

        private static void SplitLines(string text, List<string> lines, out string remainder)
        {
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

            remainder = text.Substring(start);
        }

        private void Reset()
        {
            this.stream?.Dispose();
            this.stream = null;
            this.position = 0;
            this.partialLine = string.Empty;
            this.decoder.Reset();
        }

        public void Dispose() => this.Reset();
    }
}
