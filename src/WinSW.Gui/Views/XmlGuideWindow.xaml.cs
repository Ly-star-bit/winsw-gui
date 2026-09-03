using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;

namespace WinSW.Gui.Views
{
    /// <summary>
    /// The embedded configuration cheat sheet, rendered for reading and for handing to an
    /// assistant: every code block has its own copy button, and the header can copy the whole
    /// specification as a ready-made prompt together with the configuration being edited.
    /// </summary>
    public partial class XmlGuideWindow
    {
        private static XmlGuideWindow? open;

        private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(1.8) };
        private IReadOnlyList<GuideHeading> headings = Array.Empty<GuideHeading>();
        private string? currentXml;

        private XmlGuideWindow()
        {
            this.InitializeComponent();

            this.toastTimer.Tick += (_, _) =>
            {
                this.toastTimer.Stop();
                this.Toast.BeginAnimation(OpacityProperty, Fade(0, 220));
            };

            Localizer.Changed += this.Render;
            this.Closed += (_, _) =>
            {
                Localizer.Changed -= this.Render;
                open = null;
            };

            this.Render();
        }

        /// <summary>
        /// Shows the reference, reusing the window if it is already up: it is a reference that
        /// stays open beside the editor, not a modal step.
        /// </summary>
        /// <param name="owner">The window to centre on.</param>
        /// <param name="xml">The configuration currently being edited, included in the prompt.</param>
        public static void ShowGuide(Window? owner, string? xml)
        {
            open ??= new XmlGuideWindow { Owner = owner };
            open.currentXml = xml;

            if (open.IsVisible)
            {
                open.Activate();
            }
            else
            {
                open.Show();
            }

            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
        }

        private void Render()
        {
            var document = MarkdownRenderer.Render(XmlGuide.Markdown, out this.headings);
            this.Viewer.Document = document;
            this.FillContents();
        }

        private void FillContents()
        {
            string filter = this.SearchBox.Text.Trim();

            // Only the numbered sections are worth listing; deeper headings would bury them.
            var visible = this.headings.Where(h => h.Level is >= 2 and <= 3);
            if (filter.Length > 0)
            {
                visible = visible.Where(h => h.Text.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            this.Contents.ItemsSource = visible.ToList();
        }

        private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => this.FillContents();

        private void OnHeadingPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (this.Contents.SelectedItem is GuideHeading heading)
            {
                heading.Anchor.BringIntoView();
            }
        }

        private void OnCopyAll(object sender, RoutedEventArgs e) =>
            this.CopyAndReport(XmlGuide.Markdown, "G.CopiedGuide");

        private void OnCopyPrompt(object sender, RoutedEventArgs e) =>
            this.CopyAndReport(XmlGuide.BuildPrompt(this.currentXml), "G.CopiedPrompt");

        private void OnOpenOnline(object sender, RoutedEventArgs e) => SystemShell.OpenUrl(XmlGuide.OnlineUrl);

        private void OnSaveAs(object sender, RoutedEventArgs e)
        {
            if (Dialogs.PickSaveFile(Localizer.Get("S.SaveAs"), Localizer.Get("G.MarkdownFilter"), XmlGuide.FileName) is not { } path)
            {
                return;
            }

            try
            {
                File.WriteAllText(path, XmlGuide.Markdown);
                this.ShowToast(Localizer.Format("G.Saved", Path.GetFileName(path)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                this.ShowToast(exception.Message);
            }
        }

        private void OnClose(object sender, ExecutedRoutedEventArgs e) => this.Close();

        private void OnFind(object sender, ExecutedRoutedEventArgs e) => this.SearchBox.Focus();

        private void CopyAndReport(string text, string messageKey)
        {
            this.ShowToast(SystemShell.TryCopy(text)
                ? Localizer.Format(messageKey, text.Length)
                : Localizer.Get("G.CopyFailed"));
        }

        private void ShowToast(string message)
        {
            this.ToastText.Text = message;
            this.Toast.BeginAnimation(OpacityProperty, Fade(1, 140));
            this.toastTimer.Stop();
            this.toastTimer.Start();
        }

        private static DoubleAnimation Fade(double to, int milliseconds) =>
            new(to, TimeSpan.FromMilliseconds(milliseconds));
    }
}
