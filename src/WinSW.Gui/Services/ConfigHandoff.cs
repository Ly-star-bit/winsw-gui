using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Passes a configuration to open from a second launch — "Open in WinSW" on an .xml file,
    /// or a path on the command line — to the copy already running in the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Such a launch used to start a second full console beside the one in the tray, with its
    /// own polling, its own notifications and its own webhook, so that every unexpected stop was
    /// posted to the group chat twice. Now the running copy is handed the path and opens it the
    /// way it opens one given at start, and the launch exits.
    /// </para>
    /// <para>
    /// The path goes through a named pipe, one line of UTF-8, and the running copy answers with
    /// one byte once it has taken it. The answer is what lets a launch tell a copy that took
    /// the file from one that cannot: a copy that is hung, or an older version that does not
    /// listen, leaves the launch waiting out <see cref="ConnectTimeout"/>, after which it opens
    /// the file itself as a copy of its own — no worse than before.
    /// </para>
    /// <para>
    /// Pipe names are machine-wide; there is no <c>Local\</c> for them. The session is part of
    /// <see cref="PipeName"/>, so that an account signed in twice, at the console and over
    /// remote desktop, has one pipe per session, as it has one console per session. And both
    /// ends are <see cref="PipeOptions.CurrentUserOnly"/>: a pipe of that name created by
    /// another account is not connected to, and another account cannot connect to this one.
    /// That check compares the owner of the two processes' tokens, which for an elevated
    /// process is the Administrators group rather than the user, so a launch does not reach a
    /// copy running at another elevation either: it opens the file in a console of its own,
    /// with the rights it was started with.
    /// </para>
    /// </remarks>
    public static class ConfigHandoff
    {
        /// <summary>The running copy's answer: the path was taken and is being opened.</summary>
        internal const byte Accepted = 1;

        /// <summary>The running copy's answer to something that is not a configuration's full path.</summary>
        internal const byte Refused = 0;

        /// <summary>
        /// The longest request read. A Windows path is at most 32,767 characters, and none
        /// takes more than three bytes of UTF-8.
        /// </summary>
        internal const int MaxRequestBytes = 3 * 32767;

        /// <summary>
        /// How often the running copy tries to open its pipe when the name is taken,
        /// <see cref="CreateRetryDelay"/> apart. A copy started by "Restart as administrator"
        /// can find the one it replaces still closing its pipe.
        /// </summary>
        private const int CreateAttempts = 10;

        /// <summary>How long a launch waits for the running copy's pipe before opening the file itself.</summary>
        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

        /// <summary>How long either end waits for the other once connected: one line and one byte.</summary>
        internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan CreateRetryDelay = TimeSpan.FromSeconds(1);

        /// <summary>After a failure of this end's own, a pause before listening again, so that one that repeats cannot spin.</summary>
        private static readonly TimeSpan FaultDelay = TimeSpan.FromSeconds(1);

        private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>This logon session's pipe.</summary>
        public static string PipeName
        {
            get
            {
                using var self = Process.GetCurrentProcess();
                return "WinSW.Gui.Open." + self.SessionId.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Hands <paramref name="path"/> to the copy running in this session. True when it took
        /// it; false when it could not be reached or did not answer, and this launch should open
        /// the file itself.
        /// </summary>
        public static bool TrySend(string path) => TrySend(PipeName, path, ConnectTimeout);

        /// <summary>
        /// Takes the paths later launches hand over until <paramref name="stop"/> is cancelled,
        /// calling <paramref name="open"/> with each on a thread-pool thread.
        /// </summary>
        /// <param name="failed">Told of a failure at this end, which is survived.</param>
        public static Task Listen(Action<string> open, Action<Exception> failed, CancellationToken stop) =>
            Task.Run(() => ListenAsync(PipeName, open, failed, stop));

        /// <summary>
        /// Blocks for the exchange: it is made at start, before there is a window, and the
        /// launch has nothing else to do until it knows whether to go on.
        /// </summary>
        internal static bool TrySend(string pipeName, string path, TimeSpan connectTimeout) =>
            Task.Run(() => SendAsync(pipeName, path, connectTimeout)).GetAwaiter().GetResult();

        internal static async Task<bool> SendAsync(string pipeName, string path, TimeSpan connectTimeout)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);

                using var exchange = new CancellationTokenSource(ExchangeTimeout);
                await client.WriteAsync(Utf8.GetBytes(path + "\n"), exchange.Token).ConfigureAwait(false);
                await client.FlushAsync(exchange.Token).ConfigureAwait(false);

                byte[] answer = new byte[1];
                int read = await client.ReadAsync(answer, exchange.Token).ConfigureAwait(false);
                return read == 1 && answer[0] == Accepted;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // No pipe, a pipe that is not this account's, or a copy that stopped answering.
                return false;
            }
        }

        /// <summary>
        /// Answers one launch at a time, each on a pipe instance of its own.
        /// </summary>
        /// <remarks>
        /// The next instance is opened as soon as a launch connects, before that launch is
        /// answered, so the pipe never goes away in between. A second launch arriving meanwhile
        /// connects to the new instance and is answered next; were the pipe gone, it would wait
        /// out its timeout for nothing, or, where pipes are sockets, be dropped from the queue.
        /// </remarks>
        internal static async Task ListenAsync(string pipeName, Action<string> open, Action<Exception> failed, CancellationToken stop)
        {
            NamedPipeServerStream? waiting = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    waiting ??= await CreateAsync(pipeName, failed, stop).ConfigureAwait(false);
                    if (waiting is null)
                    {
                        return;
                    }

                    using var connection = waiting;
                    waiting = null;
                    try
                    {
                        await connection.WaitForConnectionAsync(stop).ConfigureAwait(false);
                        waiting = TryCreate(pipeName, out _);
                        await ServeAsync(connection, open, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        // A launch that gave up waiting, or went away without finishing its
                        // line. It opens the file itself; this end waits for the next one.
                    }
                    catch (Exception e)
                    {
                        // A fault at this end. Recorded, and the pipe kept: a copy that holds
                        // the session but no longer listens would have every later launch wait
                        // out its timeout and then start a second console after all.
                        failed(e);
                        if (!await DelayAsync(FaultDelay, stop).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                waiting?.Dispose();
            }
        }

        /// <summary>
        /// Opens an instance of the pipe, trying again while the name is held: by the copy this
        /// one replaced, on its way out, or by a process of another account. Null when stopped,
        /// or when the name stays held; launches then meet the holder's pipe, fail to hand over
        /// and start copies of their own, as they used to.
        /// </summary>
        private static async Task<NamedPipeServerStream?> CreateAsync(string pipeName, Action<Exception> failed, CancellationToken stop)
        {
            for (int attempt = 1; ; attempt++)
            {
                if (TryCreate(pipeName, out var refusal) is { } server)
                {
                    return server;
                }

                if (attempt >= CreateAttempts)
                {
                    failed(refusal!);
                    return null;
                }

                if (!await DelayAsync(CreateRetryDelay, stop).ConfigureAwait(false))
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Two instances at most: the one being answered and the one waiting for the next launch.
        /// </summary>
        private static NamedPipeServerStream? TryCreate(string pipeName, out Exception? refusal)
        {
            try
            {
                refusal = null;
                return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                refusal = e;
                return null;
            }
        }

        /// <summary>
        /// Reads one line and answers it. The path is passed on before the answer is written,
        /// so that a launch told it was taken can exit knowing the file will open.
        /// </summary>
        private static async Task ServeAsync(Stream connection, Action<string> open, CancellationToken stop)
        {
            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(stop);
            exchange.CancelAfter(ExchangeTimeout);

            string? path = await ReadRequestAsync(connection, exchange.Token).ConfigureAwait(false);
            bool accepted = path != null && IsConfigurationPath(path);
            if (accepted)
            {
                open(path!);
            }

            await connection.WriteAsync(new[] { accepted ? Accepted : Refused }, exchange.Token).ConfigureAwait(false);
            await connection.FlushAsync(exchange.Token).ConfigureAwait(false);
        }

        /// <summary>
        /// The text up to the first line break. Null when the other end closed before one, or
        /// sent more than any path could be.
        /// </summary>
        internal static async Task<string?> ReadRequestAsync(Stream connection, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[4096];
            using var received = new MemoryStream();
            while (received.Length <= MaxRequestBytes)
            {
                int read = await connection.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }

                int end = Array.IndexOf(buffer, (byte)'\n', 0, read);
                received.Write(buffer, 0, end >= 0 ? end : read);
                if (end >= 0)
                {
                    return received.Length <= MaxRequestBytes ? Utf8.GetString(received.GetBuffer(), 0, (int)received.Length) : null;
                }
            }

            return null;
        }

        /// <summary>
        /// What a launch sends: the full path of an .xml file. Anything else is not opened. The
        /// file's existence is left to the opening, which says so in the editor when it is gone.
        /// </summary>
        internal static bool IsConfigurationPath(string path) =>
            path.Length > 0
            && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && Path.IsPathFullyQualified(path)
            && path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

        /// <summary>Waits, and says whether to go on: false once <paramref name="stop"/> is cancelled.</summary>
        private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stop)
        {
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
