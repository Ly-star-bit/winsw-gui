using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using System.Xml;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Strips the secrets out of a configuration file before it leaves the machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A diagnostics bundle exists to be handed to somebody else, and so does the XML guide's
    /// AI prompt, which goes to a third-party assistant. A WinSW configuration is allowed to
    /// hold the password of the account the service runs as, the credentials of a
    /// <c>&lt;download&gt;</c>, and whatever a <c>&lt;env&gt;</c> entry was given. Sending the
    /// file verbatim publishes all of it.
    /// </para>
    /// <para>
    /// The redaction works on the XML rather than on <see cref="Model.ServiceConfigModel"/>
    /// so that comments, formatting and any element the model does not model survive — and,
    /// more to the point, so that an element the model would silently drop is still redacted
    /// rather than quietly omitted from what the reader believes is the whole file.
    /// </para>
    /// <para>
    /// Environment values are judged by name, because most of them are the reason the bundle
    /// was collected (PATH, JAVA_HOME) and blanking them all would make it useless. The
    /// credentials in front of a URL's host are the exception, removed from every value and
    /// every text wherever they stand: <c>DATABASE_URL</c> and <c>REDIS_URL</c>, or a
    /// <c>--db postgresql://app:pw@db/app</c> argument, are how a service is usually given its
    /// database password, and none of those names says so. Every substitution is reported so
    /// the list can be shown beside the redacted file: a reader who can see what was removed
    /// can tell whether anything was missed.
    /// </para>
    /// </remarks>
    public static class ConfigRedactor
    {
        /// <summary>What a redacted value is replaced with.</summary>
        public const string Mask = "********";

        /// <summary>Elements whose text is a secret outright.</summary>
        private static readonly string[] SecretElements = { "password" };

        /// <summary>Attributes whose value is a secret outright.</summary>
        private static readonly string[] SecretAttributes = { "password" };

        /// <summary>
        /// An environment variable whose name contains one of these is treated as a secret.
        /// Matched case-insensitively, as a substring: APP_API_TOKEN and DbPassword both hit.
        /// </summary>
        private static readonly string[] SecretNameParts =
        {
            "password", "passwd", "pwd", "secret", "token", "apikey", "api_key",
            "credential", "auth", "private", "cert", "signature",
        };

        /// <summary>
        /// Elements holding a command line, where a secret is one argument among many and the
        /// rest is the most useful thing in the bundle.
        /// </summary>
        private static readonly string[] CommandLineValued = { "arguments", "startarguments", "stoparguments" };

        /// <summary>The <c>user:password@</c> of a URL's authority, if it has one.</summary>
        /// <remarks>
        /// Everything between the <c>//</c> and the last <c>@</c> before the path: a password
        /// with an <c>@</c> of its own is often written unescaped, and the drivers take the last
        /// one as the end of it. A user name alone counts too, since a token is often passed as
        /// one (<c>https://ghp_…@github.com</c>).
        /// </remarks>
        private static readonly Regex UserInfo = new(
            @"(?<=//)[^/?#\s]+@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A URL with credentials in front of its host, taken apart.</summary>
        private static readonly Regex UrlWithUserInfo = new(
            @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*:)?//(?<userinfo>[^/?#\s]+)@(?<host>[^/?#\s@""']*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>A URL whose credentials <see cref="Redact"/> masked, taken apart.</summary>
        private static readonly Regex MaskedUrl = new(
            @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*:)?//" + Regex.Escape(Mask) + @"@(?<host>[^/?#\s@""']*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// A secret passed as <c>name=value</c> on a command line: <c>-Dpassword=x</c>,
        /// <c>--api-key x</c>, <c>/PWD:x</c>. The name is kept and the value replaced, so the
        /// argument is still recognisable as the one that was there.
        /// </summary>
        /// <remarks>
        /// Both separators are accepted: <c>-Dpassword=x</c> and <c>--api-key x</c> are equally
        /// common. The space form can take one argument too many — <c>--token --verbose</c>
        /// masks the switch after it — and that is the direction to err in: masking something
        /// that was not a secret costs a line of the bundle, missing one costs the secret.
        /// </remarks>
        private static readonly Regex CommandLineSecret = new(
            @"(?<name>(?:password|passwd|pwd|secret|token|apikey|api[-_]?key|credential|accesskey)(?:\s*[=:]\s*|\s+))(?<value>""[^""]*""|'[^']*'|\S+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns <paramref name="xml"/> with its secrets masked.
        /// </summary>
        /// <param name="xml">The configuration file's text.</param>
        /// <param name="removed">
        /// What was masked, one entry per substitution, named well enough to be recognised
        /// (<c>serviceaccount/password</c>, <c>env[DB_PASSWORD]</c>) but without the value.
        /// </param>
        /// <returns>
        /// The redacted XML, or a placeholder when the file does not parse: a file this code
        /// cannot read is a file whose secrets it cannot find, and passing it through unread
        /// is the one outcome that must not happen.
        /// </returns>
        public static string Redact(string xml, out IReadOnlyList<string> removed)
        {
            if (TryRedact(xml, out removed, out var error) is { } redacted)
            {
                return redacted;
            }

            // Only where it failed, never what it read there: an XmlException message can
            // quote the text that confused the parser, and this code exists precisely
            // because that text may be a password.
            removed = new[] { "(whole file: it does not parse)" };
            return "<!-- The configuration could not be parsed, so it could not be checked for\n"
                + "     secrets and has been left out of this bundle. Attach it by hand if\n"
                + "     it holds nothing sensitive.\n\n"
                + $"     The parser stopped at line {error!.LineNumber}, position {error.LinePosition}. -->";
        }

        /// <summary>
        /// Returns <paramref name="xml"/> with its secrets masked, or null when it does not
        /// parse, for a caller that would rather leave the configuration out than explain why.
        /// </summary>
        /// <param name="xml">The configuration file's text.</param>
        /// <param name="removed">What was masked, as <see cref="Redact"/> reports it; empty when the text does not parse.</param>
        public static string? TryRedact(string xml, out IReadOnlyList<string> removed) =>
            TryRedact(xml, out removed, out _);

        /// <summary>
        /// <paramref name="earlier"/> when <paramref name="pasted"/> is nothing but the mask and
        /// there is a real earlier value to give back; otherwise <paramref name="pasted"/> as it is.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The way back from an assistant: the configuration went into the prompt masked, and
        /// the answer comes back with the mask where each secret was. Applied as written, the
        /// answer would set the service account's password to eight asterisks. These methods
        /// give each masked value back from the configuration the answer replaces.
        /// </para>
        /// <para>
        /// Which earlier value belongs to which place is the caller's to decide (it knows the
        /// fields); what these methods decide is how much of the value was the secret. A mask
        /// that has nothing to be given back from stays a mask, for the caller to count.
        /// </para>
        /// </remarks>
        [return: NotNullIfNotNull(nameof(pasted))]
        public static string? Unmask(string? pasted, string? earlier) =>
            pasted?.Trim() == Mask && !string.IsNullOrEmpty(earlier) && !earlier.Contains(Mask, StringComparison.Ordinal)
                ? earlier
                : pasted;

        /// <summary>
        /// <paramref name="pasted"/> with the <c>user:password@</c> of each URL in
        /// <paramref name="earlier"/> back in place of the mask, in front of every URL that names
        /// the same scheme, host and port.
        /// </summary>
        /// <remarks>
        /// Only in front of the same host: credentials go back to the server they were for, never
        /// to another address the answer put in the same place. The path may differ, and so may
        /// everything around the URL, which is what lets this work on a whole command line or an
        /// environment value as well as on a URL alone. Two URLs to the same host get their
        /// credentials back in the order they had them.
        /// </remarks>
        [return: NotNullIfNotNull(nameof(pasted))]
        public static string? UnmaskUrl(string? pasted, string? earlier)
        {
            if (pasted is null || earlier is null || !pasted.Contains("//" + Mask + "@", StringComparison.Ordinal))
            {
                return pasted;
            }

            var credentials = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (Match url in UrlWithUserInfo.Matches(earlier))
            {
                string userInfo = url.Groups["userinfo"].Value;
                if (userInfo.Contains(Mask, StringComparison.Ordinal))
                {
                    continue;
                }

                string key = HostKey(url);
                if (!credentials.TryGetValue(key, out var queue))
                {
                    credentials[key] = queue = new Queue<string>();
                }

                queue.Enqueue(userInfo);
            }

            return MaskedUrl.Replace(pasted, url =>
                credentials.TryGetValue(HostKey(url), out var queue) && queue.Count > 0
                    ? url.Groups["scheme"].Value + "//" + queue.Dequeue() + "@" + url.Groups["host"].Value
                    : url.Value);

            static string HostKey(Match url) => url.Groups["scheme"].Value + "//" + url.Groups["host"].Value;
        }

        /// <summary>
        /// <paramref name="pasted"/> with each argument masked as <c>-Dpassword=********</c> given
        /// back the value the same argument has in <paramref name="earlier"/>.
        /// </summary>
        /// <remarks>
        /// Argument by argument rather than the whole line: the line is where an assistant is
        /// most likely to have changed something, and that change is what was asked for. The
        /// argument is known by everything in front of its value (<c>-Ddb.password=</c>, not
        /// just <c>password=</c>), so two passwords on one line cannot swap; when the same
        /// argument appears twice, the values go back in order.
        /// </remarks>
        [return: NotNullIfNotNull(nameof(pasted))]
        public static string? UnmaskCommandLine(string? pasted, string? earlier)
        {
            if (pasted is null || earlier is null || !pasted.Contains(Mask, StringComparison.Ordinal))
            {
                return pasted;
            }

            var values = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
            foreach (Match match in CommandLineSecret.Matches(earlier))
            {
                string key = ArgumentKey(earlier, match);
                if (!values.TryGetValue(key, out var queue))
                {
                    values[key] = queue = new Queue<string>();
                }

                queue.Enqueue(match.Groups["value"].Value);
            }

            return CommandLineSecret.Replace(pasted, match =>
            {
                if (match.Groups["value"].Value.Trim('"', '\'') != Mask
                    || !values.TryGetValue(ArgumentKey(pasted, match), out var queue)
                    || queue.Count == 0)
                {
                    return match.Value;
                }

                string value = queue.Dequeue();
                return value.Contains(Mask, StringComparison.Ordinal) ? match.Value : match.Groups["name"].Value + value;
            });
        }

        /// <summary>
        /// Masks the <c>user:password@</c> of a URL the way <see cref="Redact"/> does, so a URL
        /// from the form and the same URL from a masked answer can be compared.
        /// </summary>
        [return: NotNullIfNotNull(nameof(value))]
        public static string? MaskUrl(string? value) =>
            value is null ? null : StripUserInfo(value) ?? value;

        /// <summary>How many times the mask appears in <paramref name="value"/>.</summary>
        public static int CountMasks(string? value)
        {
            int count = 0;
            int at = value?.IndexOf(Mask, StringComparison.Ordinal) ?? -1;
            while (at >= 0)
            {
                count++;
                at = value!.IndexOf(Mask, at + Mask.Length, StringComparison.Ordinal);
            }

            return count;
        }

        private static string? TryRedact(string xml, out IReadOnlyList<string> removed, out XmlException? error)
        {
            var report = new List<string>();
            removed = report;
            error = null;

            var document = new XmlDocument { PreserveWhitespace = true };
            try
            {
                document.LoadXml(xml);
            }
            catch (XmlException e)
            {
                error = e;
                return null;
            }

            if (document.DocumentElement is { } root)
            {
                Walk(root, report);
            }

            return document.OuterXml;
        }

        private static void Walk(XmlElement element, List<string> report)
        {
            RedactAttributes(element, report);

            if (Matches(element.LocalName, SecretElements) && HasText(element))
            {
                element.InnerText = Mask;
                report.Add(Path(element));
            }
            else if (HasText(element))
            {
                // A command line is searched for a secret passed by name first; then any text at
                // all, a command line included, loses the credentials in front of a URL's host.
                string text = element.InnerText;
                string redacted = text;
                if (Matches(element.LocalName, CommandLineValued) && MaskCommandLineSecrets(redacted) is { } masked)
                {
                    redacted = masked;
                    report.Add(Path(element) + " (a secret passed as an argument)");
                }

                if (StripUserInfo(redacted) is { } stripped)
                {
                    redacted = stripped;
                    report.Add(Path(element) + " (credentials in the URL)");
                }

                if (!ReferenceEquals(redacted, text))
                {
                    element.InnerText = redacted;
                }
            }

            foreach (var child in element.ChildNodes)
            {
                if (child is XmlElement childElement)
                {
                    Walk(childElement, report);
                }
            }
        }

        private static void RedactAttributes(XmlElement element, List<string> report)
        {
            // An <env> entry is a name/value pair, so the decision is made from its sibling
            // attribute rather than from the attribute's own name.
            bool secretEnvironmentEntry =
                string.Equals(element.LocalName, "env", StringComparison.OrdinalIgnoreCase)
                && Matches(element.GetAttribute("name"), SecretNameParts, substring: true);

            foreach (var attribute in element.Attributes)
            {
                if (attribute is not XmlAttribute item || item.Value.Length == 0)
                {
                    continue;
                }

                if (Matches(item.LocalName, SecretAttributes)
                    || (secretEnvironmentEntry && string.Equals(item.LocalName, "value", StringComparison.OrdinalIgnoreCase)))
                {
                    item.Value = Mask;
                    report.Add(Describe(element, item));
                }
                else if (StripUserInfo(item.Value) is { } stripped)
                {
                    item.Value = stripped;
                    report.Add(Describe(element, item) + " (credentials in the URL)");
                }
            }
        }

        /// <summary>
        /// Masks the value of any argument that names itself a secret, or null if there is
        /// none to mask.
        /// </summary>
        /// <remarks>
        /// A guess, and known to be one. A command line cannot be parsed into arguments
        /// reliably enough to do better, and blanking the whole element would take away the
        /// single most useful line in the bundle — the arguments are what a service usually
        /// failed to start on. A secret passed positionally, or under a name that does not
        /// say so, is not found; the note beside the file says as much.
        /// </remarks>
        private static string? MaskCommandLineSecrets(string value)
        {
            if (!CommandLineSecret.IsMatch(value))
            {
                return null;
            }

            return CommandLineSecret.Replace(value, m => m.Groups["name"].Value + Mask);
        }

        /// <summary>
        /// A secret argument's name as it stands on its command line: the argument from its
        /// first character up to its value, spaces closed up and case ignored.
        /// </summary>
        private static string ArgumentKey(string line, Match match)
        {
            int start = match.Index;
            while (start > 0 && !char.IsWhiteSpace(line[start - 1]))
            {
                start--;
            }

            string name = line.Substring(start, match.Groups["value"].Index - start);
            return string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        }

        /// <summary>
        /// Masks the <c>user:password@</c> of every URL in <paramref name="value"/>, or null if
        /// there is none still to mask.
        /// </summary>
        private static string? StripUserInfo(string value)
        {
            bool stripped = false;
            string result = UserInfo.Replace(value, match =>
            {
                stripped |= match.Value != Mask + "@";
                return Mask + "@";
            });

            return stripped ? result : null;
        }

        /// <summary>True when the element has text of its own rather than child elements.</summary>
        private static bool HasText(XmlElement element) =>
            !element.IsEmpty && element.InnerText.Trim().Length > 0 && element.SelectSingleNode("*") is null;

        private static bool Matches(string name, string[] candidates, bool substring = false)
        {
            foreach (string candidate in candidates)
            {
                bool hit = substring
                    ? name.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase);

                if (hit)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>"service/serviceaccount/password", for the report.</summary>
        private static string Path(XmlNode node)
        {
            var parts = new Stack<string>();
            for (var current = node; current is XmlElement; current = current.ParentNode)
            {
                parts.Push(current.LocalName);
            }

            return string.Join("/", parts);
        }

        /// <summary>"download@password", or "env[DB_PASSWORD]@value" when the entry is named.</summary>
        private static string Describe(XmlElement element, XmlAttribute attribute)
        {
            string name = element.GetAttribute("name");
            string subject = name.Length > 0 ? $"{Path(element)}[{name}]" : Path(element);
            return subject + "@" + attribute.LocalName;
        }
    }
}
