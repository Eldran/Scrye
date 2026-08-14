using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Scrye.App.Services;

/// <summary>
/// Plays trigger/MSP sounds without any package dependency.
///
/// <para>Windows uses winmm's PlaySound (async, fire-and-forget). Everywhere else there is
/// no equivalent single call, so this shells out to whichever standard player is present —
/// <c>afplay</c> on macOS, <c>paplay</c> / <c>aplay</c> / <c>play</c> on Linux — and takes
/// the first one that launches. That is cruder than a P/Invoke, but it matches what this
/// class already promises: fire-and-forget, never throw, silence when it cannot help.</para>
///
/// <para>Sound references resolve as: "beep" → the system sound; absolute path → played
/// as-is; bare name → looked up under %APPDATA%/Scrye/sounds/&lt;mud&gt;/ then
/// %APPDATA%/Scrye/sounds/ (".wav" appended when missing).</para>
/// </summary>
public static class SoundService
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_ALIAS = 0x00010000;
    private const uint SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>Root of the user sounds folder: %APPDATA%/Scrye/sounds.</summary>
    public static string SoundsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "sounds");

    /// <summary>Play a sound reference for a world. Never throws; unknown files are silent.</summary>
    public static void Play(string sound, string? mudName = null)
    {
        if (string.IsNullOrWhiteSpace(sound)) return;
        try
        {
            bool beep = sound.Equals("beep", StringComparison.OrdinalIgnoreCase);

            if (OperatingSystem.IsWindows())
            {
                if (beep)
                {
                    PlaySoundW("SystemDefault", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
                    return;
                }
                string? winPath = Resolve(sound.Trim(), mudName);
                if (winPath is not null)
                    PlaySoundW(winPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                return;
            }

            // Unix: "beep" has no alias to ask for, so it resolves to a system sound FILE and
            // then takes the same path as any other sound.
            string? file = beep ? FirstExisting(BeepCandidates()) : Resolve(sound.Trim(), mudName);
            if (file is null) return;

            foreach ((string exe, string[] args) in Players(file))
                if (TryStart(exe, args)) return;
        }
        catch { /* never let audio break the client */ }
    }

    /// <summary>Players to try, in order, on the current non-Windows platform. The first one
    /// that launches wins; a missing binary throws on Start and simply moves to the next.</summary>
    private static IEnumerable<(string Exe, string[] Args)> Players(string file)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return ("afplay", new[] { file });
            yield break;
        }
        yield return ("paplay", new[] { file });          // PulseAudio / PipeWire — the desktop default
        yield return ("aplay", new[] { "-q", file });     // ALSA — WAV only, but always present
        yield return ("play", new[] { "-q", file });      // SoX, if the user happens to have it
    }

    /// <summary>System sounds standing in for Windows' "SystemDefault" alias. Every entry is a
    /// stock file from a default desktop install; if none exist, "beep" is simply silent.</summary>
    private static IEnumerable<string> BeepCandidates()
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Sounds/Ping.aiff";
            yield return "/System/Library/Sounds/Tink.aiff";
            yield break;
        }
        // .oga plays through paplay (libsndfile); the ALSA fallback is WAV, hence the last one.
        yield return "/usr/share/sounds/freedesktop/stereo/bell.oga";
        yield return "/usr/share/sounds/freedesktop/stereo/message.oga";
        yield return "/usr/share/sounds/alsa/Front_Center.wav";
    }

    /// <summary>Launch a player and return without waiting — the point is async playback. The
    /// Process handle is disposed immediately; that does not kill the child, it only releases
    /// the handle, and .NET reaps the process when it exits.
    ///
    /// <para>Arguments go through ArgumentList rather than a command string, so a sounds folder
    /// with a space in its name (a MUD called "Discworld MUD", say) cannot break the call or
    /// smuggle anything into a shell — there is no shell involved.</para></summary>
    private static bool TryStart(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }   // not installed, or refused to launch
    }

    private static string? FirstExisting(IEnumerable<string> paths)
    {
        foreach (string p in paths) if (File.Exists(p)) return p;
        return null;
    }

    private static string? Resolve(string sound, string? mudName)
    {
        if (Path.IsPathRooted(sound)) return File.Exists(sound) ? sound : null;

        string root = SoundsDirectory();
        foreach (string candidate in Candidates(sound, mudName, root))
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates(string sound, string? mudName, string root)
    {
        bool hasExt = sound.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(mudName))
        {
            yield return Path.Combine(root, mudName, sound);
            if (!hasExt) yield return Path.Combine(root, mudName, sound + ".wav");
        }
        yield return Path.Combine(root, sound);
        if (!hasExt) yield return Path.Combine(root, sound + ".wav");
    }
}
