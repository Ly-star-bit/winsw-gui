using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.Localization;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class ConfigEditorView : UserControl
    {
        private readonly PreviewPaneWidth previewWidth = new();
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
                this.attached.PropertyChanged -= this.OnViewModelPropertyChanged;
            }

            this.attached = e.NewValue as ConfigEditorViewModel;

            if (this.attached != null)
            {
                this.attached.TrialOutput.CollectionChanged += this.OnTrialOutputChanged;
                this.attached.PropertyChanged += this.OnViewModelPropertyChanged;
                this.ApplyPreviewWidth();
            }
        }

        // Keyboard users land on Cancel when the confirmation opens; Enter is bound to the action.
        // The preview's column follows its check box.
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ConfigEditorViewModel.ConfirmVisible) && this.attached?.ConfirmVisible == true)
            {
                this.Dispatcher.BeginInvoke(() => this.ConfirmCancelButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
            else if (e.PropertyName == nameof(ConfigEditorViewModel.ShowPreview))
            {
                this.ApplyPreviewWidth();
            }
        }

        // A hidden preview gives its column back to the form. Collapsing the preview does not
        // shrink a column with a width of its own, so the column is set to nothing, and to the
        // width the splitter left it at when the preview comes back. Width rather than
        // ActualWidth: the splitter writes the one, and the other is 0 until the first layout.
        private void ApplyPreviewWidth()
        {
            if (this.attached is null)
            {
                return;
            }

            var column = this.PreviewColumn;
            double current = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
            double width = this.attached.ShowPreview ? this.previewWidth.Show(current) : this.previewWidth.Hide(current);
            column.Width = new GridLength(width);
        }

        // The preview takes no more than leaves the form its minimum: a fixed-width column does
        // not give way when the window narrows. A maximum rather than a new width, so the width
        // it was dragged to comes back as the window widens again.
        private void OnBodySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                var splitter = this.PreviewSplitter;
                double splitterWidth = splitter.Width + splitter.Margin.Left + splitter.Margin.Right;
                this.PreviewColumn.MaxWidth = PreviewPaneWidth.MaximumBeside(e.NewSize.Width, this.FormColumn.MinWidth, splitterWidth);
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
        // Scrolling to an item finds it by comparing it with the list's items, and the lines are
        // compared as the same line, not the same text: a restart loop printing the same lines
        // again scrolls to the newest copy rather than back to the first.
        private void OnTrialOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            int count = this.TrialOutputList.Items.Count;
            if (e.Action == NotifyCollectionChangedAction.Add && count > 0)
            {
                this.TrialOutputList.ScrollIntoView(this.TrialOutputList.Items[count - 1]);
            }
        }

        // Ctrl+C copies the highlighted lines, as in the log viewer. Top to bottom, as they are
        // on screen: the selection lists them in the order they were clicked.
        private void OnTrialOutputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && this.TrialOutputList.SelectedItems.Count > 0)
            {
                var picked = new HashSet<LogLine>(this.TrialOutputList.SelectedItems.OfType<LogLine>());
                string text = string.Join(
                    System.Environment.NewLine,
                    this.TrialOutputList.Items.OfType<LogLine>().Where(picked.Contains).Select(line => line.Text));

                try
                {
                    Clipboard.SetText(text);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    // The clipboard is momentarily owned by another process.
                }

                e.Handled = true;
            }
        }
    }
}
