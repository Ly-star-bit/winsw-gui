using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What a failed send says, and how often the unattended alert tries before it gives up.
    /// </summary>
    public class AlertFailureTextTests
    {
        [Fact]
        public void EveryMessageInTheChainIsKeptOutermostFirst()
        {
            var failure = new HttpRequestException(
                "An error occurred while sending the request.",
                new IOException("Unable to read data from the transport connection.", new Exception("An existing connection was forcibly closed by the remote host.")));

            Assert.Equal(
                "An error occurred while sending the request. — Unable to read data from the transport connection. — An existing connection was forcibly closed by the remote host.",
                AlertWebhook.DescribeFailure(failure));
        }

        [Fact]
        public void AMessageRepeatedByItsWrapperIsSaidOnce()
        {
            var failure = new HttpRequestException("No such host is known. (qyapi.weixin.qq.com:443)", new Exception("No such host is known."));

            Assert.Equal("No such host is known. (qyapi.weixin.qq.com:443) — No such host is known.", AlertWebhook.DescribeFailure(failure));
            Assert.Equal("same", AlertWebhook.DescribeFailure(new Exception("same", new Exception(" same "))));
        }

        [Fact]
        public void ATimeoutSaysSo()
        {
            var failure = new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.",
                new TimeoutException("A task was canceled."));

            Assert.StartsWith("The request was canceled due to the configured HttpClient.Timeout of 10 seconds elapsing.", AlertWebhook.DescribeFailure(failure), StringComparison.Ordinal);
        }

        [Fact]
        public void AFirstSuccessIsNotRetried()
        {
            var waits = new List<TimeSpan>();
            int calls = 0;

            string? error = UnattendedAlertRun.SendWithRetriesAsync(
                () => { calls++; return Task.FromResult<string?>(null); },
                d => { waits.Add(d); return Task.CompletedTask; }).GetAwaiter().GetResult();

            Assert.Null(error);
            Assert.Equal(1, calls);
            Assert.Empty(waits);
        }

        [Fact]
        public void AFailureIsTriedAgainAfterEachDelayUntilItGoesThrough()
        {
            var waits = new List<TimeSpan>();
            var answers = new Queue<string?>(new[] { "No such host is known.", "No such host is known.", null });

            string? error = UnattendedAlertRun.SendWithRetriesAsync(
                () => Task.FromResult(answers.Dequeue()),
                d => { waits.Add(d); return Task.CompletedTask; }).GetAwaiter().GetResult();

            Assert.Null(error);
            Assert.Equal(UnattendedAlertRun.RetryDelays, waits);
        }

        [Fact]
        public void AfterTheLastAttemptItsFailureIsReported()
        {
            int calls = 0;

            string? error = UnattendedAlertRun.SendWithRetriesAsync(
                () => Task.FromResult<string?>("HTTP 50" + calls++),
                _ => Task.CompletedTask).GetAwaiter().GetResult();

            Assert.Equal(UnattendedAlertRun.RetryDelays.Length + 1, calls);
            Assert.Equal("HTTP 502", error);
        }
    }
}
