using System.Windows.Input;

namespace CCAutoApprove.App.Commands;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> execute;
    private readonly Func<Exception, Task> errorHandler;
    private readonly Func<bool>? canExecute;
    private int isExecuting;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<Exception, Task> errorHandler,
        Func<bool>? canExecute = null)
        : this(WrapLegacyExecute(execute), errorHandler, canExecute)
    {
    }

    public AsyncRelayCommand(
        Func<object?, Task> execute,
        Func<Exception, Task> errorHandler,
        Func<bool>? canExecute = null)
    {
        this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
        this.errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
        this.canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref isExecuting) == 0 && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter) || Interlocked.CompareExchange(ref isExecuting, 1, 0) != 0)
        {
            return;
        }

        OnCanExecuteChanged();
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            await errorHandler(exception);
        }
        finally
        {
            Volatile.Write(ref isExecuting, 0);
            OnCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => OnCanExecuteChanged();

    private static Func<object?, Task> WrapLegacyExecute(Func<Task> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return _ => execute();
    }

    private void OnCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
