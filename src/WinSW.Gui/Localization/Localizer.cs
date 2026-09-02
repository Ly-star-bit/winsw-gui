using System;
using System.Globalization;
using System.Linq;
using System.Windows;
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
    /// and re-raises its computed properties on <see cref="Changed"/>.
    /// </remarks>
    public static class Localizer
    {
        public static readonly Language[] Languages =
        {
            new("en", "English"),
            new("zh-CN", "中文"),
        };

        public static event Action? Changed;

        public static Language Current { get; private set; } = Languages[0];

        /// <summary>Loads the saved preference, falling back to the OS display language.</summary>
        public static void Initialize()
        {
            string code = AppSettings.Current.Language
                ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en");

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

            Current = language;

            if (persist)
            {
                AppSettings.Current.Language = language.Code;
                AppSettings.Current.Save();
            }

            Changed?.Invoke();
        }

        /// <summary>Returns the string for <paramref name="key"/>, or the key itself when missing.</summary>
        public static string Get(string key) =>
            Application.Current?.TryFindResource(key) as string ?? key;

        public static string Format(string key, params object?[] args) =>
            string.Format(CultureInfo.CurrentCulture, Get(key), args);

        private static Language Find(string code) =>
            Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Languages[0];

        private static bool IsStringsDictionary(ResourceDictionary dictionary) =>
            dictionary.Source?.OriginalString.Contains("/Localization/Strings.", StringComparison.OrdinalIgnoreCase) == true;
    }
}
