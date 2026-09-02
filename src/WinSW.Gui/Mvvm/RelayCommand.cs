using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace WinSW.Gui.Mvvm
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> execute;
        private readonly Func<object?, bool>? canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => this.canExecute is null || this.canExecute(parameter);

        public void Execute(object? parameter) => this.execute(parameter);

        public void RaiseCanExecuteChanged() => this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// An async command that blocks re-entry while the operation is in flight. Service
    /// operations round-trip through UAC and the SCM, so double invocation is a real risk.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> execute;
        private readonly Func<object?, bool>? canExecute;
        private bool running;

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            this.execute = execute;
            this.canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) =>
            !this.running && (this.canExecute is null || this.canExecute(parameter));

        public async void Execute(object? parameter)
        {
            this.running = true;
            this.RaiseCanExecuteChanged();
            try
            {
                await this.execute(parameter);
            }
            finally
            {
                this.running = false;
                this.RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => this.CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
