using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using WinSW.Gui.Localization;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// The configuration cheat sheet that ships inside the executable.
    /// </summary>
    /// <remarks>
    /// The document is embedded rather than downloaded so it is available on the isolated
    /// machines where services are usually configured, and so it can never disagree with the
    /// wrapper version in the box. The source of truth is <c>docs/xml-config-cheatsheet*.md</c>
    /// in the repository; the project file embeds those files directly.
    /// </remarks>
    public static class XmlGuide
    {
        /// <summary>
        /// Public address of the same document, in the language being shown, for the
        /// "open online" button.
        /// </summary>
        public static string OnlineUrl => ProjectLinks.Doc(
            ResourceFor(Localizer.Current.Code) == "zh-CN"
                ? "xml-config-cheatsheet.zh-CN.md"
                : "xml-config-cheatsheet.md");

        private static readonly Dictionary<string, string> Cache = new();

        /// <summary>The cheat sheet in the closest language to the current interface language.</summary>
        public static string Markdown => Load(ResourceFor(Localizer.Current.Code));

        /// <summary>A suggested file name for "save as".</summary>
        public static string FileName =>
            ResourceFor(Localizer.Current.Code) == "zh-CN"
                ? "winsw-xml-cheatsheet.zh-CN.md"
                : "winsw-xml-cheatsheet.md";

        /// <summary>
        /// The whole specification plus the instructions that turn it into a prompt: paste
        /// the result into an assistant, describe the program, and get a configuration back.
        /// </summary>
        public static string BuildPrompt(string? currentXml) => BuildPrompt(currentXml, out _);

        /// <summary>
        /// The prompt, and what had to be masked in the configuration to send it.
        /// </summary>
        /// <param name="currentXml">The configuration being edited, which goes in with its secrets masked.</param>
        /// <param name="masked">What was masked in it, as <see cref="ConfigRedactor.Redact"/> reports it.</param>
        public static string BuildPrompt(string? currentXml, out IReadOnlyList<string> masked)
        {
            var builder = new StringBuilder(Markdown.TrimEnd());

            builder.Append("\n\n---\n\n");
            builder.Append(Localizer.Get("G.Prompt.Task")).Append("\n\n");

            if (ConfigurationForPrompt(currentXml, out masked) is { } configuration)
            {
                builder.Append(Localizer.Get("G.Prompt.Current")).Append("\n\n```xml\n");
                builder.Append(configuration).Append("\n```\n\n");

                // The mask is only any use on the way back if the answer keeps it where it was:
                // the editor gives the real value back to whatever still reads ********.
                if (masked.Count > 0)
                {
                    builder.Append(Localizer.Get("G.Prompt.Masked")).Append("\n\n");
                }
            }

            builder.Append(Localizer.Get("G.Prompt.Describe")).Append('\n');

            return builder.ToString();
        }

        /// <summary>
        /// The configuration as it may go into a prompt, or null when there is none to include.
        /// </summary>
        /// <remarks>
        /// The prompt is pasted into a third-party assistant, so the configuration goes through
        /// the same redaction as a diagnostics bundle: the service account's password, download
        /// credentials, <c>user:password@</c> in a URL and secret-looking variables and
        /// arguments are masked. Text that is not XML at all — the preview's "could not be
        /// rendered" note — is left out rather than passed on unread.
        /// </remarks>
        internal static string? ConfigurationForPrompt(string? currentXml, out IReadOnlyList<string> masked)
        {
            masked = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(currentXml))
            {
                return null;
            }

            return ConfigRedactor.TryRedact(currentXml, out masked)?.Trim();
        }

        private static string ResourceFor(string languageCode) =>
            languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";

        private static string Load(string code)
        {
            if (Cache.TryGetValue(code, out string? cached))
            {
                return cached;
            }

            string text;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WinSW.Gui.Guide." + code + ".md"))
            {
                text = stream is null
                    ? "# " + Localizer.Get("G.Title") + "\n\n" + OnlineUrl
                    : new StreamReader(stream, Encoding.UTF8).ReadToEnd();
            }

            Cache[code] = text;
            return text;
        }
    }
}
