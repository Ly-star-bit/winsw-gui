using System.Collections.Specialized;
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
                this.attached.Lines.CollectionChanged -= this.OnLinesChanged;
            }

            this.attached = e.NewValue as LogViewerViewModel;

            if (this.attached != null)
            {
                this.attached.Lines.CollectionChanged += this.OnLinesChanged;
            }
        }

        // Following the tail is a view concern: the view model only knows whether it is on.
        private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (this.attached?.AutoScroll != true || e.Action != NotifyCollectionChangedAction.Add)
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
