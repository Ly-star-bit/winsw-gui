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
