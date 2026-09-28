using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using WinSW.Gui.Services;

namespace WinSW.Gui.Localization
{
    /// <summary>One selectable UI language.</summary>
    public sealed class Language
    {
        public Language(string code, string nativeName)
        {
            this.Code = code;
            this.NativeName = nativeName;
        }

        /// <summary>The suffix of the <c>Strings.&lt;code&gt;.xaml</c> dictionary.</summary>
        public string Code { get; }

        /// <summary>Shown in the language picker, in the language itself.</summary>
        public string NativeName { get; }

        public override string ToString() => this.NativeName;
    }

    /// <summary>
    /// Runtime language switching.
    /// </summary>
    /// <remarks>
    /// Every user-visible string lives in <c>Localization/Strings.&lt;code&gt;.xaml</c> as a
    /// keyed resource. XAML reads them through <c>DynamicResource</c>, so swapping the merged
    /// dictionary re-renders the whole UI in place. Code reads them through <see cref="Get"/>
    /// and re-raises its computed properties on <see cref="Changed"/>. The same dictionaries
    /// carry the <c>BodyFont</c> and <c>MonoFont</c> of each language, which follow along.
    /// </remarks>
    public static class Localizer
    {
        public static readonly Language[] Languages =
        {
            new("en", "English"),
            new("zh-CN", "简体中文"),
            new("zh-TW", "繁體中文"),
            new("ja", "日本語"),
        };

        /// <summary>The language WPF is told the text is in; null until the first <see cref="Apply(Language, bool)"/>.</summary>
        private static XmlLanguage? textLanguage;

        public static event Action? Changed;

        public static Language Current { get; private set; } = Languages[0];

        /// <summary>Loads the saved preference, falling back to the OS display language.</summary>
        public static void Initialize()
        {
            string code = AppSettings.Current.Language ?? DefaultFor(CultureInfo.CurrentUICulture);

            Apply(Find(code), persist: false);
        }

        public static void Apply(Language language) => Apply(language, persist: true);

        private static void Apply(Language language, bool persist)
        {
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var replacement = new ResourceDictionary
            {
                Source = new Uri($"/WinSW.Gui;component/Localization/Strings.{language.Code}.xaml", UriKind.Relative),
            };

            var existing = dictionaries.FirstOrDefault(IsStringsDictionary);
            if (existing != null)
            {
                // Replace in place so precedence relative to the theme dictionaries is unchanged.
                dictionaries[dictionaries.IndexOf(existing)] = replacement;
            }
            else
            {
                dictionaries.Add(replacement);
            }

            var culture = CultureInfo.GetCultureInfo(language.Code);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;

            ApplyTextLanguage(culture);

            Current = language;

            if (persist)
            {
                AppSettings.Current.Language = language.Code;
                AppSettings.Current.Save();
            }

            Changed?.Invoke();
        }

        /// <summary>Tells WPF which language the text on screen is in.</summary>
        /// <remarks>
        /// <para>
        /// A character none of the named fonts has is drawn from WPF's own fallback font, which
        /// picks a face by this language. It is en-US unless told otherwise, and for en-US that
        /// fallback tries Japanese fonts for Han characters before Chinese ones, so a Chinese
        /// log line came out in a mix of Japanese and Chinese glyph shapes. The
        /// <c>BodyFont</c> and <c>MonoFont</c> of each language name the right face first;
        /// this settles whatever they leave to the fallback.
        /// </para>
        /// <para>
        /// The default every element starts from can be set only once, and only before the
        /// first element exists, so that happens at startup. A later switch is set on each open
        /// window, which its contents inherit, and on each window, tooltip and menu that
        /// appears afterwards: tooltips and menus open in popups of their own, outside the
        /// window they belong to, and the tray menu belongs to no window at all.
        /// </para>
        /// </remarks>
        private static void ApplyTextLanguage(CultureInfo culture)
        {
            var language = XmlLanguage.GetLanguage(culture.IetfLanguageTag);

            if (textLanguage is null)
            {
                textLanguage = language;
                FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(language));

                var follow = new RoutedEventHandler((sender, _) => FollowTextLanguage(sender as FrameworkElement));
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, follow);
                EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, follow);
                EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, follow);
                return;
            }

            textLanguage = language;
            foreach (Window window in Application.Current.Windows)
            {
                FollowTextLanguage(window);
            }
        }

        /// <summary>Sets the current text language on a root that has another.</summary>
        private static void FollowTextLanguage(FrameworkElement? root)
        {
            if (root != null && textLanguage != null
                && !string.Equals(root.Language?.IetfLanguageTag, textLanguage.IetfLanguageTag, StringComparison.OrdinalIgnoreCase))
            {
                root.Language = textLanguage;
            }
        }

        /// <summary>Returns the string for <paramref name="key"/>, or the key itself when missing.</summary>
        public static string Get(string key) =>
            Application.Current?.TryFindResource(key) as string ?? key;

        public static string Format(string key, params object?[] args) =>
            string.Format(CultureInfo.CurrentCulture, Get(key), args);

        /// <summary>Maps the OS display language onto a shipped dictionary.</summary>
        private static string DefaultFor(CultureInfo culture)
        {
            string name = culture.Name;
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                // Traditional-script regions: Taiwan, Hong Kong, Macao; everything else Simplified.
                return name.EndsWith("TW", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("HK", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("MO", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    ? "zh-TW" : "zh-CN";
            }

            return culture.TwoLetterISOLanguageName == "ja" ? "ja" : "en";
        }

        private static Language Find(string code) =>
            Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

        private static bool IsStringsDictionary(ResourceDictionary dictionary) =>
            dictionary.Source?.OriginalString.Contains("/Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true;
    }
}
