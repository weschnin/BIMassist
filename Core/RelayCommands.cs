using System;
using System.Windows.Input;

namespace BIMassist.Core
{
    public class RelayCommands<T> : ICommand where T : class
    {
        private Action<T> _cmdExec;
        private Func<T, bool> _canExec;

        public event EventHandler CanExecuteChanged
        {
            add
            {
                if (_canExec != null)
                    CommandManager.RequerySuggested += value;
            }
            remove
            {
                if (_canExec != null)
                    CommandManager.RequerySuggested -= value;
            }
        }

        public RelayCommands(Action<T> cmd)
        {
            _cmdExec = cmd;
            _canExec = null;
        }

        public RelayCommands(Action<T> cmd, Func<T, bool> canExec)
        {
            _cmdExec = cmd;
            _canExec = canExec;
        }

        public bool CanExecute(T parameter)
        {
            if (_canExec != null)
                return _canExec(parameter);
            else
                return true;
        }

        public bool CanExecute(object parameter)
        {
            return CanExecute(parameter as T);
        }

        public void Execute(object parameter)
        {
            _cmdExec(parameter as T);
        }

        public void Execute(T parameter)
        {
            _cmdExec(parameter);
        }
    }
}
