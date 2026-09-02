using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WinSW.Gui.Controls
{
    /// <summary>
    /// A number and a unit for the wrapper's duration syntax ("15 sec", "2 min", "1 day").
    /// <see cref="Value"/> holds the text exactly as it goes into the XML, so the model keeps
    /// speaking the wrapper's language and only the editing surface is friendlier.
    /// </summary>
    public partial class DurationEditor : UserControl
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(DurationEditor),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        /// <summary>Canonical suffixes from XmlServiceConfig.ParseTimeSpan, one per unit.</summary>
        private static readonly string[] Units = { "ms", "sec", "min", "hour", "day" };

        private bool updating;

        public DurationEditor()
        {
            this.InitializeComponent();
            this.Unit.ItemsSource = Units;
            this.Unit.SelectedIndex = 1;
        }

        public string? Value
        {
            get => (string?)this.GetValue(ValueProperty);
            set => this.SetValue(ValueProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (DurationEditor)d;
            if (editor.updating)
            {
                return;
            }

            editor.updating = true;
            try
            {
                string text = (e.NewValue as string ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    editor.Number.Text = string.Empty;
                    return;
                }

                // Accept every spelling the wrapper accepts; normalise to the canonical unit.
                foreach (var (suffix, unit) in new[]
                {
                    ("ms", "ms"), ("secs", "sec"), ("sec", "sec"), ("mins", "min"), ("min", "min"),
                    ("hours", "hour"), ("hour", "hour"), ("hrs", "hour"), ("hr", "hour"), ("days", "day"), ("day", "day"),
                })
                {
                    if (text.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        editor.Number.Text = text.Substring(0, text.Length - suffix.Length).Trim();
                        editor.Unit.SelectedItem = unit;
                        return;
                    }
                }

                // A bare number is milliseconds.
                editor.Number.Text = text;
                editor.Unit.SelectedItem = "ms";
            }
            finally
            {
                editor.updating = false;
            }
        }

        private void OnNumberChanged(object sender, TextChangedEventArgs e) => this.Push();

        private void OnUnitChanged(object sender, SelectionChangedEventArgs e) => this.Push();

        private void Push()
        {
            if (this.updating)
            {
                return;
            }

            this.updating = true;
            try
            {
                string number = this.Number.Text.Trim();
                string unit = this.Unit.SelectedItem as string ?? "sec";

                // Leave whatever the user typed in place while it is not yet a number, so the
                // validation message can point at it rather than at a silently dropped value.
                this.Value = number.Length == 0 ? null
                    : int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? $"{number} {unit}"
                    : number;
            }
            finally
            {
                this.updating = false;
            }
        }
    }
}
