using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;

namespace WinSW.Gui.Views
{
    /// <summary>One line as the comparison window shows it: numbers as text, a gap as a sentence.</summary>
    public sealed class DiffRow
    {
        public DiffRow(DiffLine line)
        {
            this.Kind = line.Kind;
            this.Old = line.OldNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            this.New = line.NewNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            this.Marker = line.Marker;
            this.Text = line.Kind == DiffKind.Gap ? Localizer.Format("M.Diff.Unchanged", line.Count) : line.Text;
        }

        public DiffKind Kind { get; }

        public string Old { get; }

        public string New { get; }

        public string Marker { get; }

        public string Text { get; }
    }

    /// <summary>
    /// An earlier version of a configuration against the file as it is now, before it is
    /// restored. Restoring used to load the version into the form sight unseen; this shows
    /// what it would bring back and what it would drop, and restores from here.
    /// </summary>
    public partial class DiffWindow
    {
        private bool resizeBorderAttached;

        private DiffWindow(string subtitle, IReadOnlyList<DiffLine> lines, bool canRestore)
        {
            this.InitializeComponent();

            this.Subtitle.Text = subtitle;
            this.Lines.ItemsSource = lines.Select(l => new DiffRow(l)).ToList();
            if (lines.Count == 0)
            {
                this.Lines.Visibility = Visibility.Collapsed;
                this.SameNotice.Visibility = Visibility.Visible;
            }

            // Not over unsaved changes in the editor, which a restore would replace unseen.
            this.RestoreButton.IsEnabled = canRestore && lines.Count > 0;
            if (!canRestore)
            {
                this.RestoreButton.ToolTip = Localizer.Get("M.History.SaveFirst");
            }
        }

        /// <summary>Shows the comparison; true when the version is to be restored.</summary>
        public static bool ShowComparison(Window? owner, string subtitle, IReadOnlyList<DiffLine> lines, bool canRestore)
        {
            var window = new DiffWindow(subtitle, lines, canRestore) { Owner = owner };
            return window.ShowDialog() == true;
        }

        /// <summary>Same title bar, same covered border, same answer as the main window.</summary>
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            if (!this.resizeBorderAttached)
            {
                this.resizeBorderAttached = true;
                WindowResizeBorder.Attach(this);
            }
        }

        private void OnRestore(object sender, RoutedEventArgs e) => this.DialogResult = true;

        private void OnCloseClick(object sender, RoutedEventArgs e) => this.Close();

        private void OnClose(object sender, ExecutedRoutedEventArgs e) => this.Close();
    }
}
