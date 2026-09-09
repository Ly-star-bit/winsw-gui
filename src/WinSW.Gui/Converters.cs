using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Collections.Generic;
using System.Windows.Media;
using WinSW.Gui.Model;
using WinSW.Gui.ViewModels;
using WinSW.Gui.Views;

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
                Services.LogSeverity.IsError(text) ? "LogErrorBrush" :
                Services.LogSeverity.IsWarning(text) ? "LogWarnBrush" :
                "LogInfoBrush";

            return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gainsboro;
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

    /// <summary>
    /// Turns a list of samples (0–100) into the points of a sparkline. The parameter is
    /// "width,height"; defaults suit the detail panel's metric card.
    /// </summary>
    public sealed class SparklineConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            double width = 140;
            double height = 32;
            if (parameter is string spec)
            {
                var parts = spec.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                {
                    width = w;
                    height = h;
                }
            }

            var points = new PointCollection();
            if (value is not System.Collections.Generic.IReadOnlyList<double> samples || samples.Count < 2)
            {
                return points;
            }

            double step = width / (samples.Count - 1);
            for (int i = 0; i < samples.Count; i++)
            {
                double y = height - (Math.Clamp(samples[i], 0, 100) / 100.0 * (height - 2)) - 1;
                points.Add(new Point(i * step, y));
            }

            return points;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>True when the bound value is non-null; used to light up fields that have a validation message.</summary>
    public sealed class NotNullToBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string text ? text.Length > 0 : value != null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Resolves a page's view model to its view, and keeps the view.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shell used to reach its pages through DataTemplates, and a ContentControl
    /// instantiates a template every time its content changes: each visit to a page built
    /// that page's view from scratch and threw the previous one away. The services page is
    /// five hundred lines of XAML, so a switch cost tens of milliseconds of construction and
    /// layout — and, worse than the hitch, the view's own state went with it: the scroll
    /// position, the selected tab, the caret in a filter box.
    /// </para>
    /// <para>
    /// One view per view model, made on first visit and reused after. The mapping is the one
    /// the templates spelled out, kept explicit here so that a page without a view fails in
    /// plain sight rather than silently showing nothing. The bound content becomes an
    /// element rather than a model, which WPF displays as-is; the Loaded trigger that plays
    /// the entrance animation fires on every attach, so a cached page still slides in.
    /// </para>
    /// </remarks>
    public sealed class PageViewConverter : IValueConverter
    {
        private readonly Dictionary<object, FrameworkElement> views = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is null)
            {
                return null;
            }

            if (!this.views.TryGetValue(value, out var view))
            {
                view = value switch
                {
                    DashboardViewModel => new DashboardView(),
                    ConfigEditorViewModel => new ConfigEditorView(),
                    DesktopTasksViewModel => new DesktopTasksView(),
                    LogViewerViewModel => new LogViewerView(),
                    WizardViewModel => new WizardView(),
                    RemoteViewModel => new RemoteView(),

                    // The settings page edits the shell's own preferences, so it binds to the shell.
                    ShellViewModel => new SettingsView(),

                    _ => throw new NotSupportedException("No view is registered for " + value.GetType().Name),
                };

                view.DataContext = value;
                this.views[value] = view;
            }

            return view;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
