using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Scrye.App.Services;
using Scrye.App.ViewModels;
using Scrye.App.Views;

namespace Scrye.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            CrashLog.InstallUiThread(ex => vm.RaiseToast("Something went wrong",
                $"{ex.GetType().Name}: {ex.Message} - Scrye kept running. Details: {CrashLog.LogDirectory}"));
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
