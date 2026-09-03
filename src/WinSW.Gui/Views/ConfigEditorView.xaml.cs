using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
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
