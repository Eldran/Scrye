using System.Windows.Input;

namespace Scrye.App.ViewModels;

/// <summary>Tiny hand-rolled ICommand so the skeleton needs no MVVM-toolkit NuGet.
/// Swap for CommunityToolkit.Mvvm's [RelayCommand] later if desired.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A command that receives a typed parameter (e.g. a clicked grid cell).</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    public RelayCommand(Action<T> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { if (parameter is T t) _execute(t); }

    /// <summary>Required by <see cref="ICommand"/>, and deliberately inert: this command
    /// takes no can-execute predicate, so <see cref="CanExecute"/> is unconditionally true
    /// and there is never anything to announce. The empty accessors say that on purpose —
    /// a plain field event would raise CS0067 ("never used") on every build, and a warning
    /// that is expected is a warning nobody reads.
    ///
    /// <para>If a parameterised command ever needs to disable itself, give this class a
    /// <c>Func&lt;T, bool&gt;</c> and a real backing event, the way the non-generic
    /// <see cref="RelayCommand"/> has.</para></summary>
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
