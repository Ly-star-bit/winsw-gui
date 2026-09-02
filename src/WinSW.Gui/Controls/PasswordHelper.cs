using System.Windows;
using System.Windows.Controls;

namespace WinSW.Gui.Controls
{
    /// <summary>
    /// Lets a <see cref="PasswordBox"/> take part in data binding. WPF keeps
    /// <see cref="PasswordBox.Password"/> off the dependency-property system on purpose;
    /// the configuration file stores the value in clear text anyway, so masking on screen
    /// is what is being bought here, not secrecy in memory.
    /// </summary>
    public static class PasswordHelper
    {
        public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordHelper),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

        private static readonly DependencyProperty IsUpdatingProperty = DependencyProperty.RegisterAttached(
            "IsUpdating", typeof(bool), typeof(PasswordHelper), new PropertyMetadata(false));

        public static string GetBoundPassword(DependencyObject element) => (string)element.GetValue(BoundPasswordProperty);

        public static void SetBoundPassword(DependencyObject element, string value) => element.SetValue(BoundPasswordProperty, value);

        private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not PasswordBox box)
            {
                return;
            }

            box.PasswordChanged -= OnPasswordChanged;
            if (!(bool)box.GetValue(IsUpdatingProperty))
            {
                box.Password = e.NewValue as string ?? string.Empty;
            }

            box.PasswordChanged += OnPasswordChanged;
        }

        private static void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            box.SetValue(IsUpdatingProperty, true);
            SetBoundPassword(box, box.Password);
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
