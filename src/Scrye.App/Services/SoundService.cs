using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Scrye.App.Services;

/// <summary>
/// Plays trigger/MSP sounds without any package dependency. Windows: winmm's
/// PlaySound (async, fire-and-forget); other platforms: no-op for now.
/// Sound references resolve as: "beep" → system default sound; absolute path →
/// played as-is; bare name → looked up under %APPDATA%/Scrye/sounds/&lt;mud&gt;/ then
/// %APPDATA%/Scrye/sounds/ (".wav" appended when missing).
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
        if (string.IsNullOrWhiteSpace(sound) || !OperatingSystem.IsWindows()) return;
        try
        {
            if (sound.Equals("beep", StringComparison.OrdinalIgnoreCase))
            {
                PlaySoundW("SystemDefault", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
                return;
            }

            string? path = Resolve(sound.Trim(), mudName);
            if (path is not null)
                PlaySoundW(path, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
        }
        catch { /* never let audio break the client */ }
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
