using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WinSW.Gui.Mvvm
{
    /// <summary>
    /// Minimal <see cref="INotifyPropertyChanged"/> base. The GUI deliberately avoids an
    /// MVVM framework dependency; the surface it would use is this one method.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            this.Raise(propertyName);
            return true;
        }

        protected void Raise([CallerMemberName] string? propertyName = null)
        {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
