using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinSW.Gui.Model;

namespace WinSW.Gui
{
    /// <summary>Maps a service's health onto the palette's status colours.</summary>
    public sealed class HealthToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string key = value is ServiceHealth health ? health switch
            {
                ServiceHealth.Running => "HealthRunningBrush",
                ServiceHealth.Stopped => "HealthStoppedBrush",
                ServiceHealth.Pending => "HealthPendingBrush",
                ServiceHealth.Broken => "HealthBrokenBrush",
                _ => "HealthUnknownBrush",
            }
            : "HealthUnknownBrush";

            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>True becomes Collapsed. Use for "show this only when the flag is off".</summary>
    public sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public sealed class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not true;
    }

    /// <summary>Collapses an element when the bound string is null or blank.</summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapses an element when the bound reference is null.</summary>
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is null ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapses an element when a collection is empty.</summary>
    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Highlights stderr-looking log lines without needing a parser.</summary>
    public sealed class LogSeverityToBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string text = value as string ?? string.Empty;

            string key =
                Contains(text, "error") || Contains(text, "exception") || Contains(text, "fatal") ? "LogErrorBrush" :
                Contains(text, "warn") ? "LogWarnBrush" :
                "LogInfoBrush";

            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gainsboro;

            static bool Contains(string haystack, string needle) =>
                haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Compares the bound value with the parameter; used for step indicators.</summary>
    public sealed class EqualityToBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Equals(value?.ToString(), parameter?.ToString());

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
