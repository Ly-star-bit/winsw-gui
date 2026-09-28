using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// The hand-over of a configuration from a second launch to the running copy, over a pipe
    /// of the test's own. What it promises the launch: an answer of yes only when the path was
    /// taken, and a no — never a hang — when nobody is there to take it.
    /// </summary>
    public sealed class ConfigHandoffTests : IDisposable
    {
        /// <summary>Generous: a listener started a moment ago may not have its pipe open yet.</summary>
        private static readonly TimeSpan Connect = TimeSpan.FromSeconds(5);

        /// <summary>Short and unique; on Unix the name becomes part of a socket path, which has a length limit.</summary>
        private readonly string pipeName = "WinSW.Gui.Test." + Guid.NewGuid().ToString("n").Substring(0, 12);

        private readonly CancellationTokenSource stop = new();
        private readonly BlockingCollection<string> opened = new();
        private readonly BlockingCollection<ConsoleLaunch> launches = new();
        private readonly ConcurrentQueue<Exception> failures = new();
        private Task? listening;

        public void Dispose()
        {
            this.stop.Cancel();
            this.listening?.Wait(TimeSpan.FromSeconds(10));
            this.stop.Dispose();
            this.opened.Dispose();
            this.launches.Dispose();
        }

        [Fact]
        public async Task AHandedOverPathReachesTheRunningCopy()
        {
            this.Listen();
            string path = Configuration("app.xml");

            bool taken = await ConfigHandoff.SendAsync(this.pipeName, path, Connect);

            Assert.True(taken);
            Assert.Equal(path, this.NextOpened());
            Assert.Empty(this.failures);
        }

        /// <summary>A path is taken as it is, whatever it is written in.</summary>
        [Fact]
        public async Task APathOutsideAsciiArrivesIntact()
        {
            this.Listen();
            string path = Configuration(Path.Combine("服务", "配置 app.xml"));

            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, path, Connect));
            Assert.Equal(path, this.NextOpened());
        }

        [Fact]
        public async Task LaunchesOneAfterAnotherAreAllHeardInTurn()
        {
            this.Listen();
            string[] paths = { Configuration("one.xml"), Configuration("two.xml"), Configuration("three.xml") };

            foreach (string path in paths)
            {
                Assert.True(await ConfigHandoff.SendAsync(this.pipeName, path, Connect));
            }

            Assert.Equal(paths, new[] { this.NextOpened(), this.NextOpened(), this.NextOpened() });
        }

        /// <summary>The launch then opens the file itself, which is what it did before there was a hand-over.</summary>
        [Fact]
        public async Task WithNobodyListeningTheLaunchIsToldNoWithinItsTimeout()
        {
            var clock = Stopwatch.StartNew();

            bool taken = await ConfigHandoff.SendAsync(this.pipeName, Configuration("app.xml"), TimeSpan.FromMilliseconds(300));

            Assert.False(taken);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a launch must not wait long for a copy that is not there");
        }

        [Fact]
        public async Task SomethingOtherThanAConfigurationsFullPathIsRefused()
        {
            this.Listen();

            Assert.False(await ConfigHandoff.SendAsync(this.pipeName, "app.xml", Connect));
            Assert.False(await ConfigHandoff.SendAsync(this.pipeName, Configuration("notes.txt"), Connect));

            // Still listening, and nothing was opened for either.
            string path = Configuration("app.xml");
            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, path, Connect));
            Assert.Equal(path, this.NextOpened());
        }

        /// <summary>
        /// A launch that gives up halfway — killed, or out of time — must not take the pipe
        /// with it: every later launch would then start a second console after all.
        /// </summary>
        [Fact]
        public async Task ALaunchThatGoesAwayMidLineDoesNotEndTheListening()
        {
            this.Listen();

            using (var client = new NamedPipeClientStream(".", this.pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await client.ConnectAsync((int)Connect.TotalMilliseconds);
                byte[] half = Encoding.UTF8.GetBytes(Configuration("half"));
                await client.WriteAsync(half);
                await client.FlushAsync();
            }

            string path = Configuration("app.xml");
            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, path, Connect));
            Assert.Equal(path, this.NextOpened());
            Assert.Empty(this.failures);
        }

        [Fact]
        public async Task OnceStoppedTheCopyTakesNothingMore()
        {
            this.Listen();
            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, Configuration("app.xml"), Connect));

            this.stop.Cancel();
            Assert.True(this.listening!.Wait(TimeSpan.FromSeconds(10)), "the listening should end when told to");

            Assert.False(await ConfigHandoff.SendAsync(this.pipeName, Configuration("later.xml"), TimeSpan.FromMilliseconds(300)));
        }

        [Fact]
        public async Task ARequestIsTheTextUpToTheLineBreak()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("C:\\svc\\app.xml\nignored"));

            Assert.Equal("C:\\svc\\app.xml", await ConfigHandoff.ReadRequestAsync(stream, CancellationToken.None));
        }

        [Fact]
        public async Task ARequestCutShortIsNoRequest()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("C:\\svc\\app.x"));

            Assert.Null(await ConfigHandoff.ReadRequestAsync(stream, CancellationToken.None));
        }

        [Fact]
        public async Task ARequestLongerThanAnyPathIsNoRequest()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', ConfigHandoff.MaxRequestBytes + 1) + "\n"));

            Assert.Null(await ConfigHandoff.ReadRequestAsync(stream, CancellationToken.None));
        }

        [Fact]
        public void OnlyAFullPathToAnXmlFileIsAConfigurationPath()
        {
            Assert.True(ConfigHandoff.IsConfigurationPath(Configuration("app.xml")));
            Assert.True(ConfigHandoff.IsConfigurationPath(Configuration("APP.XML")));
            Assert.False(ConfigHandoff.IsConfigurationPath("app.xml"));
            Assert.False(ConfigHandoff.IsConfigurationPath(Path.Combine("svc", "app.xml")));
            Assert.False(ConfigHandoff.IsConfigurationPath(Configuration("app.txt")));
            Assert.False(ConfigHandoff.IsConfigurationPath(string.Empty));
        }

        /// <summary>
        /// A launch that says who it is reaches the running copy as a launch, configuration and
        /// all, and is answered at once: the copy decides afterwards whether to come forward or to
        /// offer to hand over, and the launch must not wait on a question put to the user.
        /// </summary>
        [Fact]
        public async Task ALaunchThatSaysWhoItIsReachesTheRunningCopyAsALaunch()
        {
            this.ListenForLaunches();
            string config = Configuration("app.xml");
            var sent = new ConsoleLaunch("1.3.0", Path.Combine(Path.GetTempPath(), "winsw-handoff", "WinSW.Gui-win-x64.exe"), config);

            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, sent.ToRequest(), Connect));

            Assert.True(this.launches.TryTake(out var heard, TimeSpan.FromSeconds(10)), "the launch should have been heard");
            Assert.Equal(sent.Version, heard!.Version);
            Assert.Equal(sent.ExecutablePath, heard.ExecutablePath);
            Assert.Equal(config, heard.ConfigPath);
            Assert.Empty(this.opened);
        }

        /// <summary>A bare path still arrives as one, from a launch that says nothing else.</summary>
        [Fact]
        public async Task ACopyListeningForLaunchesStillTakesABarePath()
        {
            this.ListenForLaunches();
            string path = Configuration("app.xml");

            Assert.True(await ConfigHandoff.SendAsync(this.pipeName, path, Connect));

            Assert.Equal(path, this.NextOpened());
            Assert.Empty(this.launches);
        }

        /// <summary>
        /// Refused, the launch knows the copy could not be told who it is, and looks for it by its
        /// process instead; see RunningCopy.
        /// </summary>
        [Fact]
        public async Task ACopyThatDoesNotListenForLaunchesRefusesOne()
        {
            this.Listen();
            var launch = new ConsoleLaunch("1.3.0", Path.Combine(Path.GetTempPath(), "winsw-handoff", "WinSW.Gui.exe"), null);

            Assert.False(await ConfigHandoff.SendAsync(this.pipeName, launch.ToRequest(), Connect));
            Assert.Empty(this.opened);
        }

        /// <summary>
        /// The longest launch there can be: two paths of the longest length Windows allows, in
        /// characters that take three bytes each.
        /// </summary>
        [Fact]
        public async Task TheLongestLaunchFitsInARequest()
        {
            string longest = new string('\u670D', 32767);
            string line = new ConsoleLaunch("1.3.0-ci.1234", longest, longest).ToRequest();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));

            Assert.Equal(line, await ConfigHandoff.ReadRequestAsync(stream, CancellationToken.None));
        }

        /// <summary>A full path on whichever system runs the tests; the file need not exist.</summary>
        private static string Configuration(string name) => Path.Combine(Path.GetTempPath(), "winsw-handoff", name);

        private void Listen() =>
            this.listening = Task.Run(() => ConfigHandoff.ListenAsync(this.pipeName, this.opened.Add, this.failures.Enqueue, this.stop.Token));

        private void ListenForLaunches() =>
            this.listening = Task.Run(() => ConfigHandoff.ListenAsync(this.pipeName, this.opened.Add, this.launches.Add, this.failures.Enqueue, this.stop.Token));

        private string NextOpened()
        {
            Assert.True(this.opened.TryTake(out string? path, TimeSpan.FromSeconds(10)), "a path should have been opened");
            return path!;
        }
    }
}
