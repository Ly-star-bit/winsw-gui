using System.Windows;
using System.Windows.Controls;
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
