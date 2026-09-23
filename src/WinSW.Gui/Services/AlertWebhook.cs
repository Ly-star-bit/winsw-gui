using System;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WinSW.Gui.Localization;
using WinSW.Gui.Model;

namespace WinSW.Gui.Services
{
    /// <summary>The chat robots the alert knows the shape of; anything else gets a plain JSON body.</summary>
    public enum WebhookKind
    {
        Generic,
        WeCom,
        DingTalk,
        Feishu,
    }

    /// <summary>One request to a webhook: where it goes and what is posted.</summary>
    public sealed record WebhookRequest(string Url, string Body);

    /// <summary>
    /// Posts to a group chat when a service stops unexpectedly: the same moment the tray
    /// notification is raised, to wherever the people who look after the machine are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kind of robot is told from the address. WeCom, DingTalk and Feishu each want their
    /// own JSON; each signing scheme below is written from the vendor's published sample, and
    /// the tests pin them to what those samples produce. DingTalk signs <c>timestamp\nsecret</c>
    /// with the secret as the key, in milliseconds, and takes the result in the query string;
    /// Feishu uses <c>timestamp\nsecret</c> itself as the key over an empty message, in seconds,
    /// and takes the result in the body. Anything else gets <c>{"text": …}</c>, which Slack and
    /// most general-purpose receivers accept.
    /// </para>
    /// <para>
    /// The address carries the robot's key and the secret signs for it, so both are kept in
    /// the settings file encrypted to the Windows user; see <see cref="ProtectedText"/>.
    /// </para>
    /// <para>
    /// A failure is recorded in the action log and nowhere else. An alert that could not be
    /// sent is not a reason to put a dialog in front of someone at the console — who, if they
    /// are there, already has the tray notification.
    /// </para>
    /// </remarks>
    public static class AlertWebhook
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        /// <summary>
        /// Chinese text and a Base64 signature's plus signs are posted as they are, not as
        /// \u escapes: the body goes to a robot, never into a page, so there is nothing for the
        /// default encoder's HTML caution to protect.
        /// </summary>
        private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

        /// <summary>The webhook address, decrypted; empty when none is set.</summary>
        public static string Url
        {
            get => ProtectedText.Unprotect(AppSettings.Current.AlertWebhook) ?? string.Empty;
            set
            {
                AppSettings.Current.AlertWebhook = ProtectedText.Protect(value.Trim());
                AppSettings.Current.Save();
            }
        }

        /// <summary>The signing secret for DingTalk or Feishu, decrypted; empty when none is set.</summary>
        public static string Secret
        {
            get => ProtectedText.Unprotect(AppSettings.Current.AlertSecret) ?? string.Empty;
            set
            {
                AppSettings.Current.AlertSecret = ProtectedText.Protect(value.Trim());
                AppSettings.Current.Save();
            }
        }

        public static WebhookKind KindOf(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return WebhookKind.Generic;
            }

            string host = uri.Host.ToLowerInvariant();
            return host switch
            {
                "qyapi.weixin.qq.com" => WebhookKind.WeCom,
                "oapi.dingtalk.com" => WebhookKind.DingTalk,
                "open.feishu.cn" or "open.larksuite.com" => WebhookKind.Feishu,
                _ => WebhookKind.Generic,
            };
        }

        /// <summary>Announces an unexpected stop, if an address is set. Never throws.</summary>
        public static async Task NotifyStopAsync(ServiceEntry entry)
        {
            if (!IsConfigured)
            {
                return;
            }

            string exitCode = entry.LastExitCodeText;
            string text = entry.CrashCount > 1
                ? Localizer.Format("M.Alert.StoppedRepeated", Environment.MachineName, entry.ServiceName, exitCode, entry.CrashCount)
                : Localizer.Format("M.Alert.Stopped", Environment.MachineName, entry.ServiceName, exitCode);

            string? error = await SendAsync(Url, Secret, text).ConfigureAwait(false);
            ActionLog.Record("alert", entry.ServiceName, error is null ? "ok" : "failed: " + error);
        }

        /// <summary>Posts <paramref name="text"/>; null when the robot accepted it, otherwise why not.</summary>
        public static async Task<string?> SendAsync(string url, string secret, string text)
        {
            WebhookRequest request;
            try
            {
                request = Build(url, secret, text, DateTimeOffset.UtcNow);
            }
            catch (UriFormatException e)
            {
                return e.Message;
            }

            try
            {
                using var content = new StringContent(request.Body, Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync(request.Url, content).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return response.IsSuccessStatusCode
                    ? Refusal(KindOf(url), body)
                    : "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + response.ReasonPhrase;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return e.Message;
            }
        }

        /// <summary>The request for one message, signed where the robot is given a secret.</summary>
        /// <exception cref="UriFormatException">The address is not an absolute http or https URL.</exception>
        internal static WebhookRequest Build(string url, string secret, string text, DateTimeOffset now)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new UriFormatException(Localizer.Format("M.Alert.BadUrl", url));
            }

            bool signed = secret.Length > 0;
            switch (KindOf(url))
            {
                case WebhookKind.WeCom:
                    return new WebhookRequest(url, JsonSerializer.Serialize(new { msgtype = "text", text = new { content = text } }, Json));

                case WebhookKind.DingTalk:
                {
                    string target = url;
                    if (signed)
                    {
                        long ms = now.ToUnixTimeMilliseconds();
                        target += (url.Contains('?') ? "&" : "?") + "timestamp=" + ms.ToString(CultureInfo.InvariantCulture) + "&sign=" + DingTalkSign(ms, secret);
                    }

                    return new WebhookRequest(target, JsonSerializer.Serialize(new { msgtype = "text", text = new { content = text } }, Json));
                }

                case WebhookKind.Feishu:
                {
                    if (!signed)
                    {
                        return new WebhookRequest(url, JsonSerializer.Serialize(new { msg_type = "text", content = new { text } }, Json));
                    }

                    long seconds = now.ToUnixTimeSeconds();
                    string timestamp = seconds.ToString(CultureInfo.InvariantCulture);
                    return new WebhookRequest(url, JsonSerializer.Serialize(new { timestamp, sign = FeishuSign(seconds, secret), msg_type = "text", content = new { text } }, Json));
                }

                default:
                    return new WebhookRequest(url, JsonSerializer.Serialize(new { text }, Json));
            }
        }

        /// <summary>DingTalk: HMAC-SHA256 of <c>timestamp\nsecret</c> keyed with the secret, Base64, URL-encoded.</summary>
        internal static string DingTalkSign(long timestampMs, string secret)
        {
            string toSign = timestampMs.ToString(CultureInfo.InvariantCulture) + "\n" + secret;
            byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(toSign));
            return Uri.EscapeDataString(Convert.ToBase64String(mac));
        }

        /// <summary>Feishu: HMAC-SHA256 keyed with <c>timestamp\nsecret</c> over nothing, Base64.</summary>
        internal static string FeishuSign(long timestampSeconds, string secret)
        {
            string key = timestampSeconds.ToString(CultureInfo.InvariantCulture) + "\n" + secret;
            byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Array.Empty<byte>());
            return Convert.ToBase64String(mac);
        }

        /// <summary>
        /// A 200 is not always an acceptance: the robots answer with their own code. Null when
        /// the robot took the message.
        /// </summary>
        internal static string? Refusal(WebhookKind kind, string body)
        {
            if (kind == WebhookKind.Generic)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                string codeName = kind == WebhookKind.Feishu ? "code" : "errcode";
                string messageName = kind == WebhookKind.Feishu ? "msg" : "errmsg";

                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty(codeName, out var code)
                    || code.ValueKind != JsonValueKind.Number
                    || code.GetInt64() == 0)
                {
                    return null;
                }

                string message = root.TryGetProperty(messageName, out var text) ? text.ToString() : string.Empty;
                return codeName + " " + code.GetInt64().ToString(CultureInfo.InvariantCulture) + (message.Length > 0 ? ": " + message : string.Empty);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Text kept in the settings file encrypted to the current Windows user (DPAPI), for values
    /// that are as good as a password: a webhook address carries its robot's key.
    /// </summary>
    public static class ProtectedText
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WinSW.Gui settings");

        public static string? Protect(string value)
        {
            if (value.Length == 0)
            {
                return null;
            }

            byte[] sealedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(sealedBytes);
        }

        /// <summary>Null when unset, or when it was sealed for another user or machine.</summary>
        public static string? Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception e) when (e is CryptographicException or FormatException)
            {
                return null;
            }
        }
    }
}
