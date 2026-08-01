using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Scrye.App.Services;

/// <summary>One pane's saved placement.</summary>
public sealed class PaneLayoutEntry
{
    public string Name { get; set; } = "";
    public string Dock { get; set; } = "Bottom";   // Bottom | Right | Floating
}

/// <summary>A dragged HUD panel's saved position (canvas coordinates).</summary>
public sealed class HudPanelLayout
{
    public string Name { get; set; } = "";   // pluginId + "|" + panel title
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>A world's saved pane setup (plus the timestamp toggle).</summary>
public sealed class WorldLayout
{
    public List<PaneLayoutEntry> Panes { get; set; } = new();
    public bool ShowTimestamps { get; set; }
    public List<HudPanelLayout> HudPanels { get; set; } = new();
}

/// <summary>
/// Persists each world's pane arrangement under %APPDATA%/Scrye/layouts/&lt;world&gt;.json,
/// so "your setup" survives reconnects and restarts. Load returns null when no
/// layout was saved yet; Save never throws (layout loss must not break the client).
/// </summary>
public static class PaneLayoutStore
{
    private static string Dir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "layouts");

    private static string PathFor(string world)
    {
        var sb = new System.Text.StringBuilder(world.Length);
        foreach (char c in world)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        string name = sb.Length == 0 ? "world" : sb.ToString();
        return Path.Combine(Dir(), name + ".json");
    }

    public static WorldLayout? Load(string world)
    {
        try
        {
            string path = PathFor(world);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WorldLayout>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static void Save(string world, WorldLayout layout)
    {
        try
        {
            Directory.CreateDirectory(Dir());
            File.WriteAllText(PathFor(world),
                JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a failed layout save must never break the session */ }
    }
}
