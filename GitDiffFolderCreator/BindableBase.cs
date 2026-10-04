using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace GitDiffFolderCreator
{
    /// <summary>Implementation of <see cref="INotifyPropertyChanged"/> to simplify models.</summary>
    public abstract class BindableBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChangedEventHandler? handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>Command that forwards to a delegate and can be re-evaluated explicitly.</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _execute(parameter);
            }
        }

        public void RaiseCanExecuteChanged()
        {
            EventHandler? handler = CanExecuteChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Asynchronous command that refuses re-entrant execution and exposes its own busy state, so a
    /// command cannot start twice and strand the UI in a running state when it faults.
    /// </summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }

            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        /// <summary>Raised when the asynchronous handler throws, so the failure is never swallowed
        /// by <see cref="Execute"/> being an <c>async void</c> ICommand entry point.</summary>
        public event EventHandler<Exception>? ExecutionFailed;

        /// <summary>
        /// Where the command was invoked. WPF raises <see cref="CanExecuteChanged"/> handlers
        /// directly against the elements, which throws if the thread is not the one that owns them.
        /// </summary>
        private readonly System.Windows.Threading.Dispatcher? _dispatcher =
            System.Windows.Threading.Dispatcher.CurrentDispatcher;

        public bool IsExecuting
        {
            get { return _isExecuting; }
            private set
            {
                if (_isExecuting == value)
                {
                    return;
                }

                _isExecuting = value;
                RaiseCanExecuteChanged();
            }
        }

        public bool CanExecute(object? parameter)
        {
            return !_isExecuting && (_canExecute == null || _canExecute(parameter));
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            IsExecuting = true;
            try
            {
                await _execute(parameter).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                EventHandler<Exception>? handler = ExecutionFailed;
                if (handler != null)
                {
                    handler(this, ex);
                }
            }
            finally
            {
                IsExecuting = false;
            }
        }

        public void RaiseCanExecuteChanged()
        {
            // An async body is free to end its awaits with ConfigureAwait(false), which puts the
            // continuation on the thread pool. The bound controls must still be updated on the
            // thread that owns them, so the notification is posted back rather than raised here.
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Normal,
                    new Action(() => RaiseCanExecuteChanged()));
                return;
            }

            EventHandler? handler = CanExecuteChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}