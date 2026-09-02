using System;
using System.IO;
using System.Text;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    public class LogTailReaderTests : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "WinSW.Gui.Tests", Guid.NewGuid().ToString("N") + ".log");

        public LogTailReaderTests()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        public void Dispose()
        {
            if (File.Exists(this.path))
            {
                File.Delete(this.path);
            }
        }

        [Fact]
        public void ReturnsOnlyCompleteLines_ThenTheRest()
        {
            File.WriteAllText(this.path, "one\r\ntwo\nthr");
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();
            Assert.Equal(new[] { "one", "two" }, lines);
            Assert.Empty(reader.ReadNewLines());

            File.AppendAllText(this.path, "ee\nfour\n");
            Assert.Equal(new[] { "three", "four" }, reader.ReadNewLines());
        }

        [Fact]
        public void DetectsTruncation()
        {
            File.WriteAllText(this.path, "a\nb\nc\n");
            using var reader = new LogTailReader(this.path);
            Assert.Equal(3, reader.ReadNewLines().Count);

            File.WriteAllText(this.path, "x\n");
            var lines = reader.ReadNewLines();
            Assert.True(reader.Restarted);
            Assert.Equal(new[] { "x" }, lines);
        }

        [Fact]
        public void AutoDetection_FallsBackToAnsiForGbk()
        {
            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(this.path, gbk.GetBytes("服务已启动\n"));

            using var reader = new LogTailReader(this.path);

            // Only meaningful when the machine's ANSI code page is GBK; elsewhere the
            // decision still must be "not UTF-8".
            var lines = reader.ReadNewLines();
            Assert.Single(lines);
            Assert.NotNull(reader.Encoding);
            Assert.NotEqual("utf-8", reader.Encoding!.WebName);
        }

        [Fact]
        public void AutoDetection_KeepsUtf8WhenValid()
        {
            File.WriteAllBytes(this.path, new UTF8Encoding(false).GetBytes("服务已启动\n"));
            using var reader = new LogTailReader(this.path);

            var lines = reader.ReadNewLines();
            Assert.Equal("服务已启动", lines[0]);
            Assert.Equal("utf-8", reader.Encoding!.WebName);
        }

        [Fact]
        public void ExplicitEncoding_IsHonoured()
        {
            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(this.path, gbk.GetBytes("中文\n"));

            using var reader = new LogTailReader(this.path, LogEncodingChoice.SystemAnsi);
            var lines = reader.ReadNewLines();

            // Decoding with the system ANSI page is only lossless where that page is GBK;
            // what must hold everywhere is that the reader used the ANSI choice, not UTF-8.
            Assert.Single(lines);
            Assert.Equal(LogTailReader.SystemAnsiEncoding.WebName, reader.Encoding!.WebName);
        }
    }
}
