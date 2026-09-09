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

        /// <summary>
        /// Raised when a command's operation throws.
        /// </summary>
        /// <remarks>
        /// <see cref="Execute"/> has to be <c>async void</c> — that is the signature
        /// <see cref="ICommand"/> gives it — and an exception escaping an <c>async void</c>
        /// method is posted to the synchronization context, where it becomes an unhandled
        /// dispatcher exception. Catching it here instead is what lets the button be
        /// re-enabled and the failure be reported as itself, rather than as a crash the
        /// application happened to survive.
        /// </remarks>
        public static event Action<Exception>? UnhandledException;

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
            catch (Exception e)
            {
                var handler = UnhandledException;
                if (handler is null)
                {
                    // Nobody is listening. Rethrowing on the dispatcher restores exactly what
                    // would have happened without this catch — the failure must not be
                    // swallowed just because the command wrapper is the one holding it.
                    throw;
                }

                handler(e);
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
