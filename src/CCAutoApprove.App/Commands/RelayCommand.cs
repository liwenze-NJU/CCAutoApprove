using System.Windows.Input;

namespace CCAutoApprove.App.Commands;

public sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
{
    private readonly Action<object?> execute = execute ?? throw new ArgumentNullException(nameof(execute));
    private readonly Predicate<object?>? canExecute = canExecute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
