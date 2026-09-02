using WinSW.Gui.ViewModels;

namespace WinSW.Gui
{
    public partial class MainWindow
    {
        public MainWindow()
        {
            this.InitializeComponent();
            this.DataContext = new ShellViewModel();
        }
    }
}
