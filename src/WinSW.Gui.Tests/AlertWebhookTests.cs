using System;
using System.Text.Json;
using WinSW.Gui.Services;
using Xunit;

namespace WinSW.Gui.Tests
{
    /// <summary>
    /// What is posted to each robot. The expected signatures were produced by the vendors' own
    /// published samples — DingTalk's and Feishu's Python — run on the same inputs, because the
    /// two schemes differ in exactly the details that are easy to get backwards.
    /// </summary>
    public class AlertWebhookTests
    {
        private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1599360473);

        [Fact]
        public void DingTalkSignsTheTimestampAndSecretWithTheSecret()
        {
            Assert.Equal("34ZfaID44JFjYdnsx63%2F8kcge%2B4CqWG8VYyrEnLGCpc%3D", AlertWebhook.DingTalkSign(1599360473000, "SECthis is secret"));
        }

        [Fact]
        public void FeishuSignsNothingWithTheTimestampAndSecret()
        {
            Assert.Equal("4OB1tNwrCa7wo7BbZnRkKWc7lI6ScctwyIwCoO67xFQ=", AlertWebhook.FeishuSign(1599360473, "demo secret"));
        }

        [Theory]
        [InlineData("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=k", WebhookKind.WeCom)]
        [InlineData("https://oapi.dingtalk.com/robot/send?access_token=t", WebhookKind.DingTalk)]
        [InlineData("https://open.feishu.cn/open-apis/bot/v2/hook/h", WebhookKind.Feishu)]
        [InlineData("https://open.larksuite.com/open-apis/bot/v2/hook/h", WebhookKind.Feishu)]
        [InlineData("https://hooks.slack.com/services/x", WebhookKind.Generic)]
        public void TheRobotIsToldFromItsAddress(string url, WebhookKind kind)
        {
            Assert.Equal(kind, AlertWebhook.KindOf(url));
        }

        [Fact]
        public void WeComTakesATextMessage()
        {
            var request = AlertWebhook.Build("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=k", string.Empty, "服务停止", At);

            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("text", body.RootElement.GetProperty("msgtype").GetString());
            Assert.Equal("服务停止", body.RootElement.GetProperty("text").GetProperty("content").GetString());
            Assert.Contains("服务停止", request.Body, StringComparison.Ordinal);
        }

        [Fact]
        public void ASignedDingTalkMessageCarriesItsSignatureInTheAddress()
        {
            var request = AlertWebhook.Build("https://oapi.dingtalk.com/robot/send?access_token=t", "SECthis is secret", "hi", At);

            Assert.Equal(
                "https://oapi.dingtalk.com/robot/send?access_token=t&timestamp=1599360473000&sign=34ZfaID44JFjYdnsx63%2F8kcge%2B4CqWG8VYyrEnLGCpc%3D",
                request.Url);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("hi", body.RootElement.GetProperty("text").GetProperty("content").GetString());
        }

        [Fact]
        public void ASignedFeishuMessageCarriesItsSignatureInTheBody()
        {
            var request = AlertWebhook.Build("https://open.feishu.cn/open-apis/bot/v2/hook/h", "demo secret", "hi", At);

            Assert.Equal("https://open.feishu.cn/open-apis/bot/v2/hook/h", request.Url);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("1599360473", body.RootElement.GetProperty("timestamp").GetString());
            Assert.Equal("4OB1tNwrCa7wo7BbZnRkKWc7lI6ScctwyIwCoO67xFQ=", body.RootElement.GetProperty("sign").GetString());
            Assert.Equal("text", body.RootElement.GetProperty("msg_type").GetString());
            Assert.Equal("hi", body.RootElement.GetProperty("content").GetProperty("text").GetString());
        }

        [Fact]
        public void WithoutASecretNothingIsSigned()
        {
            Assert.Equal("https://oapi.dingtalk.com/robot/send?access_token=t", AlertWebhook.Build("https://oapi.dingtalk.com/robot/send?access_token=t", string.Empty, "hi", At).Url);

            using var feishu = JsonDocument.Parse(AlertWebhook.Build("https://open.feishu.cn/open-apis/bot/v2/hook/h", string.Empty, "hi", At).Body);
            Assert.False(feishu.RootElement.TryGetProperty("sign", out _));
        }

        [Fact]
        public void AnyOtherReceiverGetsPlainText()
        {
            using var body = JsonDocument.Parse(AlertWebhook.Build("https://hooks.example.com/x", "ignored", "hi", At).Body);
            Assert.Equal("hi", body.RootElement.GetProperty("text").GetString());
        }

        /// <summary>A robot answers 200 and says no in the body.</summary>
        [Theory]
        [InlineData(WebhookKind.WeCom, "{\"errcode\":0,\"errmsg\":\"ok\"}", null)]
        [InlineData(WebhookKind.WeCom, "{\"errcode\":93000,\"errmsg\":\"invalid webhook url\"}", "errcode 93000: invalid webhook url")]
        [InlineData(WebhookKind.DingTalk, "{\"errcode\":310000,\"errmsg\":\"sign not match\"}", "errcode 310000: sign not match")]
        [InlineData(WebhookKind.Feishu, "{\"code\":0,\"msg\":\"success\",\"data\":{}}", null)]
        [InlineData(WebhookKind.Feishu, "{\"code\":19021,\"msg\":\"sign match fail\"}", "code 19021: sign match fail")]
        [InlineData(WebhookKind.Generic, "anything", null)]
        public void ARefusalIsReadFromTheBody(WebhookKind kind, string body, string? refusal)
        {
            Assert.Equal(refusal, AlertWebhook.Refusal(kind, body));
        }
    }
}
