using System;
using System.IO;
using System.Text.Json;

namespace Scrye.App.Services;

/// <summary>Window chrome the user arranged, remembered between runs.</summary>
public sealed class UiState
{
    /// <summary>The MUD-list sidebar is collapsed to its edge strip.</summary>
    public bool SidebarCollapsed { get; set; }
}

/// <summary>
/// Persists app-level UI state to <c>%APPDATA%/Scrye/ui-state.json</c>.
///
/// <para>Deliberately NOT part of the profile cascade: this is where a panel was left, not a
/// setting the user chose. Putting it in the Global layer would mean it only stuck when you
/// pressed Save in Settings, which is not how a collapsed panel is expected to behave.</para>
///
/// <para>Deliberately NOT in <c>layouts/</c> either, where <see cref="PaneLayoutStore"/> keeps
/// one file per world: those filenames come from world names, and a world called "ui-state"
/// would collide. Same discipline though — a failed read or write is never allowed to break
/// the client, so both sides swallow and fall back to the default.</para>
/// </summary>
public static class UiStateStore
{
    private static string PathFor() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Scrye", "ui-state.json");

    public static UiState Load()
    {
        try
        {
            string path = PathFor();
            if (!File.Exists(path)) return new UiState();
            return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path)) ?? new UiState();
        }
        catch { return new UiState(); }
    }

    public static void Save(UiState state)
    {
        try
        {
            string path = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* losing a chrome preference must never break the session */ }
    }
}
