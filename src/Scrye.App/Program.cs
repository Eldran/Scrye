using System;
using Avalonia;

namespace Scrye.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Services.CrashLog.Install();   // capture unhandled exceptions to %APPDATA%/Scrye/logs
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // an exception that escaped the UI message loop (or startup) — record it before dying
            Services.CrashLog.Write("Main/message-loop", ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
