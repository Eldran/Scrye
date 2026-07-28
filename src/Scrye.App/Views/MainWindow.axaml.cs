using Avalonia.Controls;
using Avalonia.Input;
using Scrye.App.ViewModels;

namespace Scrye.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control { DataContext: WorldViewModel vm })
        {
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
