using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinSW.Gui.ViewModels;

namespace WinSW.Gui.Views
{
    public partial class LogViewerView : UserControl
    {
        private LogViewerViewModel? attached;

        public LogViewerView()
        {
            this.InitializeComponent();
            this.DataContextChanged += this.OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.attached != null)
            {
                this.attached.LinesAppended -= this.ScrollToEnd;
                this.attached.ScrollToRequested -= this.ScrollToIndex;
            }

            this.attached = e.NewValue as LogViewerViewModel;

            if (this.attached != null)
            {
                this.attached.LinesAppended += this.ScrollToEnd;
                this.attached.ScrollToRequested += this.ScrollToIndex;
            }
        }

        // Following the tail is a view concern: the view model only knows whether it is on.
        // It fires once per batch of lines, not once per line, so a burst of output does
        // not turn into thousands of layout passes.
        // Ctrl+C copies the highlighted lines; string items would otherwise copy nothing.
        private void OnOutputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && this.Output.SelectedItems.Count > 0)
            {
                try
                {
                    Clipboard.SetText(string.Join(System.Environment.NewLine, this.Output.SelectedItems.OfType<string>()));
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

        private void ScrollToIndex(int index)
        {
            if (index < 0 || index >= this.Output.Items.Count)
            {
                return;
            }

            this.Output.SelectedIndex = index;
            this.Output.ScrollIntoView(this.Output.Items[index]);
        }

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
