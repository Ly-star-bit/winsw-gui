using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using WinSW.Gui.Localization;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class ConfigEditorView : UserControl
    {
        private ConfigEditorViewModel? attached;

        public ConfigEditorView()
        {
            this.InitializeComponent();
            this.DataContextChanged += this.OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.attached != null)
            {
                this.attached.TrialOutput.CollectionChanged -= this.OnTrialOutputChanged;
            }

            this.attached = e.NewValue as ConfigEditorViewModel;

            if (this.attached != null)
            {
                this.attached.TrialOutput.CollectionChanged += this.OnTrialOutputChanged;
            }
        }

        // The XML reference is a window of its own: it stays open beside the editor while
        // a configuration is being written, and carries the configuration into the prompt
        // it can put on the clipboard.
        private void OnOpenGuide(object sender, RoutedEventArgs e) =>
            XmlGuideWindow.ShowGuide(Window.GetWindow(this), (this.DataContext as ConfigEditorViewModel)?.XmlPreview);

        // The History menu is read as it opens: the folder behind it changes with every save.
        // Its items are built here rather than through an item container style, so that they
        // take the theme's menu item style as they are, with no style key to go missing.
        // Picking one compares it with the file; restoring is done from the comparison, with
        // what it would change in front of whoever is restoring it.
        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.ContextMenu is { } menu && this.DataContext is ConfigEditorViewModel editor)
            {
                editor.RefreshHistory();
                menu.Items.Clear();
                foreach (var entry in editor.History)
                {
                    var item = new MenuItem { Header = entry.Label, IsEnabled = entry.CanCompare };
                    item.Click += (_, _) => this.Compare(editor, entry);
                    menu.Items.Add(item);
                }

                menu.PlacementTarget = button;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        private void Compare(ConfigEditorViewModel editor, HistoryEntry entry)
        {
            if (entry.Version is not { } version || editor.CompareWithFile(version) is not { } lines)
            {
                return;
            }

            string subtitle = Localizer.Format("M.Diff.Subtitle", version.SavedAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), editor.FilePath);
            if (DiffWindow.ShowComparison(Window.GetWindow(this), subtitle, lines, entry.CanRestore))
            {
                editor.RestoreVersionCommand.Execute(entry);
            }
        }

        // The trial-run panel always follows its output; it is short-lived and interactive.
        private void OnTrialOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            int count = this.TrialOutputList.Items.Count;
            if (e.Action == NotifyCollectionChangedAction.Add && count > 0)
            {
                this.TrialOutputList.ScrollIntoView(this.TrialOutputList.Items[count - 1]);
            }
        }
    }
}
