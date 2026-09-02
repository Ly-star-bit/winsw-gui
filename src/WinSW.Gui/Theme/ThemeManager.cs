using System;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using WinSW.Gui.Services;

namespace WinSW.Gui.Theme
{
    public enum ThemeChoice
    {
        System,
        Light,
        Dark,
    }

    /// <summary>
    /// Light / dark switching at runtime, mirroring the language mechanism: the palette
    /// dictionary is swapped in place and WPF-UI is told to restyle its controls.
    /// </summary>
    public static class ThemeManager
    {
        public static event Action? Changed;

        public static ThemeChoice Current { get; private set; } = ThemeChoice.System;

        /// <summary>The theme actually on screen after resolving <see cref="ThemeChoice.System"/>.</summary>
        public static bool IsDark { get; private set; } = true;

        /// <summary>True when Windows' high-contrast mode is on and the palette follows it.</summary>
        public static bool IsHighContrast { get; private set; }

        public static void Initialize()
        {
            var choice = Enum.TryParse(AppSettings.Current.Theme, true, out ThemeChoice saved) ? saved : ThemeChoice.System;
            Apply(choice, persist: false);

            // Follow the OS while "system" is selected.
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(Current, persist: false));
                }
            };
        }

        public static void Apply(ThemeChoice choice) => Apply(choice, persist: true);

        private static void Apply(ThemeChoice choice, bool persist)
        {
            bool dark = choice switch
            {
                ThemeChoice.Dark => true,
                ThemeChoice.Light => false,
                _ => SystemPrefersDark(),
            };

            // High contrast is an accessibility setting, not a taste: it overrides the
            // light/dark choice while it is on.
            bool highContrast = SystemParameters.HighContrast;
            string file = highContrast ? "Palette.HighContrast.xaml" : dark ? "Palette.xaml" : "Palette.Light.xaml";
            if (highContrast)
            {
                dark = true;
            }

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var palette = new ResourceDictionary
            {
                Source = new Uri($"/WinSW.Gui;component/Theme/{file}", UriKind.Relative),
            };

            var existing = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("/Theme/Palette", StringComparison.OrdinalIgnoreCase) == true);
            if (existing != null)
            {
                dictionaries[dictionaries.IndexOf(existing)] = palette;
            }
            else
            {
                dictionaries.Add(palette);
            }

            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
                dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
                Wpf.Ui.Controls.WindowBackdropType.Mica,
                false);

            Current = choice;
            IsDark = dark;
            IsHighContrast = highContrast;

            if (persist)
            {
                AppSettings.Current.Theme = choice.ToString().ToLowerInvariant();
                AppSettings.Current.Save();
            }

            Changed?.Invoke();
        }

        /// <summary>Reads the same switch the Settings app writes for "Choose your default app mode".</summary>
        private static bool SystemPrefersDark()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception e) when (e is System.Security.SecurityException or System.IO.IOException)
            {
                return true;
            }
        }
    }
}
