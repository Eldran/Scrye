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

    /// <summary>Where crash logs go, for telling the user.</summary>
    public static string LogDirectory => LogDir;

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

    /// <summary>
    /// Catch what a UI-thread handler throws (a button command, a binding, a timer tick): log
    /// it, tell the user, and keep running. Without this, nothing caught them and any one of
    /// them closed the whole client - opening Settings with a damaged global.json did, which
    /// is exactly what the startup toast tells you to do (release review, 21 Oct 2026).
    ///
    /// <para>A burst is the exception: more than <see cref="BurstLimit"/> inside
    /// <see cref="BurstWindow"/> means something is failing every frame, the app is not
    /// coming back, and carrying on would only hide it - that one is left unhandled.</para>
    /// </summary>
    public static void InstallUiThread(Action<Exception> tell)
    {
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("Dispatcher.UIThread.UnhandledException", e.Exception);
            DateTime now = DateTime.UtcNow;
            lock (Burst)
            {
                Burst.Enqueue(now);
                while (Burst.Count > 0 && now - Burst.Peek() > BurstWindow) Burst.Dequeue();
                if (Burst.Count > BurstLimit) return;          // a loop: let it end the app
            }
            e.Handled = true;
            if (now - _lastTold < TimeSpan.FromSeconds(10)) return;   // one toast, not a stack
            _lastTold = now;
            try { tell(e.Exception); } catch { /* the reporter must not throw from here */ }
        };
    }

    private const int BurstLimit = 20;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(10);
    private static readonly System.Collections.Generic.Queue<DateTime> Burst = new();
    private static DateTime _lastTold = DateTime.MinValue;

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
