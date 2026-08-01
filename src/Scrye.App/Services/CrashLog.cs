using System;
using System.IO;
using System.Threading.Tasks;

namespace Scrye.App.Services;

/// <summary>
/// Last-resort crash logging. Writes unhandled exceptions to
/// <c>%APPDATA%/Scrye/logs/crash-*.log</c> so a crash on a user's machine leaves a trace
/// instead of the window just vanishing. Wire it up first thing in Main; it must never
/// throw itself (a failed log write is swallowed).
/// </summary>
public static class CrashLog
{
    private static string LogDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "logs");

    /// <summary>Subscribe to the process-wide unhandled-exception sources.</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Append an exception (with context) to a timestamped crash log. Never throws.</summary>
    public static void Write(string context, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            string file = Path.Combine(LogDir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            string body =
                $"[{DateTime.Now:O}] {context}{Environment.NewLine}" +
                $"App: Scrye   OS: {Environment.OSVersion}   .NET: {Environment.Version}{Environment.NewLine}" +
                (ex?.ToString() ?? "(no exception object)") + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(file, body);
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>Run <paramref name="action"/>, logging (and swallowing) any exception so a
    /// plugin/UI mishap can't crash the whole client. Returns true if it ran cleanly.</summary>
    public static bool Guard(string context, Action action)
    {
        try { action(); return true; }
        catch (Exception ex) { Write(context, ex); return false; }
    }
}
