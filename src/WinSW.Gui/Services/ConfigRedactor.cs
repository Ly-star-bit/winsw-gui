using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml;

namespace WinSW.Gui.Services
{
    /// <summary>
    /// Strips the secrets out of a configuration file before it leaves the machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A diagnostics bundle exists to be handed to somebody else, and a WinSW configuration
    /// is allowed to hold the password of the account the service runs as, the credentials of
    /// a <c>&lt;download&gt;</c>, and whatever a <c>&lt;env&gt;</c> entry was given. Sending
    /// the file verbatim publishes all of it.
    /// </para>
    /// <para>
    /// The redaction works on the XML rather than on <see cref="Model.ServiceConfigModel"/>
    /// so that comments, formatting and any element the model does not model survive — and,
    /// more to the point, so that an element the model would silently drop is still redacted
    /// rather than quietly omitted from what the reader believes is the whole file.
    /// </para>
    /// <para>
    /// Environment values are judged by name, because most of them are the reason the bundle
    /// was collected (PATH, JAVA_HOME) and blanking them all would make it useless. Every
    /// substitution is reported so the list can be shown beside the redacted file: a reader
    /// who can see what was removed can tell whether anything was missed.
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
        /// Attributes and elements holding a URL, which may carry <c>user:password@</c> in
        /// front of the host.
        /// </summary>
        private static readonly string[] UrlValued = { "from", "to", "proxy" };

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
        private static readonly Regex UserInfo = new(
            @"(?<=//)[^/@\s]*:[^/@\s]*@", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            var report = new List<string>();
            removed = report;

            var document = new XmlDocument { PreserveWhitespace = true };
            try
            {
                document.LoadXml(xml);
            }
            catch (XmlException e)
            {
                // Only where it failed, never what it read there: an XmlException message can
                // quote the text that confused the parser, and this code exists precisely
                // because that text may be a password.
                report.Add("(whole file: it does not parse)");
                return "<!-- The configuration could not be parsed, so it could not be checked for\n"
                    + "     secrets and has been left out of this bundle. Attach it by hand if\n"
                    + "     it holds nothing sensitive.\n\n"
                    + $"     The parser stopped at line {e.LineNumber}, position {e.LinePosition}. -->";
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
            else if (Matches(element.LocalName, UrlValued) && HasText(element))
            {
                if (StripUserInfo(element.InnerText) is { } stripped)
                {
                    element.InnerText = stripped;
                    report.Add(Path(element) + " (credentials in the URL)");
                }
            }
            else if (Matches(element.LocalName, CommandLineValued) && HasText(element))
            {
                if (MaskCommandLineSecrets(element.InnerText) is { } masked)
                {
                    element.InnerText = masked;
                    report.Add(Path(element) + " (a secret passed as an argument)");
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
                else if (Matches(item.LocalName, UrlValued) && StripUserInfo(item.Value) is { } stripped)
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

        /// <summary>Removes <c>user:password@</c> from a URL, or null if there is none.</summary>
        private static string? StripUserInfo(string value)
        {
            var match = UserInfo.Match(value);
            return match.Success ? UserInfo.Replace(value, Mask + "@") : null;
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
