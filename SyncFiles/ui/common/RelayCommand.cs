using System;
using System.Windows.Input; // Required for ICommand
namespace SyncFiles.UI.Common // 或者 SyncFiles.VSIX.UI.Common, SyncFiles.UI.ViewModels 等
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;
        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        public RelayCommand(Action execute)
            : this(o => execute(), null) // Call the other constructor
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
        }
        public RelayCommand(Action execute, Func<bool> canExecute)
             : this(o => execute(), o => canExecute()) // Call the other constructor
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            if (canExecute == null) throw new ArgumentNullException(nameof(canExecute));
        }
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }
        public void Execute(object parameter)
        {
            _execute(parameter);
        }
        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}