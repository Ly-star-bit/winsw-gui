using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.Services;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class LogViewerView : UserControl
    {
        private LogViewerViewModel? attached;

        /// <summary>The window whose keys F3 and Shift+F3 are taken from while the page is on screen.</summary>
        private Window? keyWindow;

        public LogViewerView()
        {
            this.InitializeComponent();
            this.DataContextChanged += this.OnDataContextChanged;
            this.Loaded += this.OnLoaded;
            this.Unloaded += this.OnUnloaded;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.attached != null)
            {
                this.attached.LinesAppended -= this.ScrollToEnd;
                this.attached.ScrollToRequested -= this.ScrollToIndex;
                this.attached.PropertyChanged -= this.OnViewModelPropertyChanged;
            }

            this.attached = e.NewValue as LogViewerViewModel;

            if (this.attached != null)
            {
                this.attached.LinesAppended += this.ScrollToEnd;
                this.attached.ScrollToRequested += this.ScrollToIndex;
                this.attached.PropertyChanged += this.OnViewModelPropertyChanged;
            }
        }

        // F3 and Shift+F3 are listened for on the window rather than bound on the page: a key
        // binding on the page fires only while something on it has the focus, and after the
        // page is picked in the rail the focus is still on the rail. The page is in the window
        // only while it is the one shown — the shell swaps its view in and out — so Loaded and
        // Unloaded are exactly when the keys are the page's. Let go of first, in case Loaded
        // comes twice.
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            this.ReleaseKeys();
            this.keyWindow = Window.GetWindow(this);
            if (this.keyWindow != null)
            {
                this.keyWindow.KeyDown += this.OnWindowKeyDown;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => this.ReleaseKeys();

        private void ReleaseKeys()
        {
            if (this.keyWindow != null)
            {
                this.keyWindow.KeyDown -= this.OnWindowKeyDown;
                this.keyWindow = null;
            }
        }

        // Bubbled, not previewed, so anything on the way that has a use for F3 comes first.
        // Nothing jumps behind the cleanup confirmation.
        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            var modifiers = Keyboard.Modifiers;
            if (e.Handled || e.Key != Key.F3 || (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift)
                || this.attached is null || this.attached.CleanupConfirmVisible)
            {
                return;
            }

            var command = modifiers == ModifierKeys.Shift ? this.attached.PreviousErrorCommand : this.attached.NextErrorCommand;
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            e.Handled = true;
        }

        // Keyboard users land on Cancel when the confirmation opens; Enter is bound to Delete.
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogViewerViewModel.CleanupConfirmVisible) && this.attached?.CleanupConfirmVisible == true)
            {
                this.Dispatcher.BeginInvoke(() => this.CleanupCancelButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        // Ctrl+C copies the highlighted lines, which a list of data items would otherwise not
        // do. Top to bottom, as they are on screen: the selection lists them in the order they
        // were clicked.
        private void OnOutputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && this.Output.SelectedItems.Count > 0)
            {
                var picked = new HashSet<LogLine>(this.Output.SelectedItems.OfType<LogLine>());
                string text = string.Join(
                    System.Environment.NewLine,
                    this.Output.Items.OfType<LogLine>().Where(picked.Contains).Select(line => line.Text));

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

        // Ctrl+wheel zooms the log text, like a browser.
        private void OnOutputMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && this.attached != null)
            {
                this.attached.FontSize += e.Delta > 0 ? 1 : -1;
                e.Handled = true;
            }
        }

        // Scrolling to an item finds it by comparing it with the list's items. Log lines are
        // compared as the same line, not the same text, so this lands on the line asked for
        // and not on the first earlier line that reads the same.
        private void ScrollToIndex(int index)
        {
            if (index < 0 || index >= this.Output.Items.Count)
            {
                return;
            }

            object line = this.Output.Items[index];
            if (this.OutputTab.IsSelected)
            {
                this.ShowLine(line);
                return;
            }

            // An error jump from the events tab, by F3 or the toolbar button: the lines are on
            // the other tab, whose list is laid out only once that tab is shown. The line rather
            // than its index is carried over, since more lines may come in meanwhile.
            this.OutputTab.IsSelected = true;
            this.Dispatcher.BeginInvoke(() => this.ShowLine(line), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ShowLine(object line)
        {
            if (!this.Output.Items.Contains(line))
            {
                // Pushed out of the buffer, or the file was changed, before the tab came up.
                return;
            }

            this.Output.SelectedItem = line;
            this.Output.ScrollIntoView(line);
        }

        // Following the tail is a view concern: the view model only knows whether it is on.
        // It fires once per batch of lines, not once per line, so a burst of output does
        // not turn into thousands of layout passes.
        private void ScrollToEnd()
        {
            if (this.attached?.AutoScroll != true)
            {
                return;
            }

            int count = this.Output.Items.Count;
            if (count > 0)
            {
                this.Output.ScrollIntoView(this.Output.Items[count - 1]);
            }
        }
    }
}
