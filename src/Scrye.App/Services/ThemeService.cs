using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Scrye.App.Services;

/// <summary>A named app color scheme: Fluent theme variant, accent, and the Scrye
/// neutral palette. <see cref="ToString"/> returns the display name so ComboBoxes
/// can render schemes without an item template.</summary>
public sealed record ThemeScheme(
    string Key,
    string DisplayName,
    bool IsDark,
    Color Accent,
    Color Bg,
    Color Panel,
    Color PanelAlt,
    Color Line,
    Color Text,
    Color TextDim,
    Color InsetBg)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Applies a <see cref="ThemeScheme"/> app-wide by swapping the resources declared
/// in App.axaml (SystemAccentColor* + the Scrye* brushes) and setting the Fluent
/// theme variant. Everything that consumes the palette does so via DynamicResource,
/// so a scheme change takes effect immediately — no restart.
///
/// The terminal output surface (ScryeOutputBg and OutputView's own fill) is
/// intentionally constant: MUD ANSI colors are designed for a dark background,
/// so the game output stays dark in every scheme, including Light.
/// </summary>
public static class ThemeService
{
    /// <summary>The fixed terminal surface color (matches OutputView's fill).</summary>
    private static readonly Color OutputBg = Color.FromRgb(0x10, 0x14, 0x1A);
    /// <summary>The fixed terminal text colour — used for the command input line so the
    /// interactive output area reads the same in every scheme (matching the game output,
    /// whose per-run colours are already theme-independent).</summary>
    private static readonly Color OutputText = Color.FromRgb(0xD6, 0xDE, 0xE8);

    public static readonly IReadOnlyList<ThemeScheme> Schemes = new[]
    {
        //              key         display               dark   accent        bg            panel         panelAlt      line          text          textDim       inset
        new ThemeScheme("slate",    "Slate (dark · cyan)",   true,  C("#35C4D6"), C("#14181F"), C("#1B212B"), C("#222A36"), C("#2C3542"), C("#D6DEE8"), C("#8A97A8"), C("#10141A")),
        new ThemeScheme("light",    "Light",                 false, C("#0E8DA0"), C("#F2F4F7"), C("#FFFFFF"), C("#E9EDF2"), C("#C9D2DD"), C("#1D2530"), C("#5B6878"), C("#FAFBFD")),
        new ThemeScheme("midnight", "Midnight (indigo)",     true,  C("#7C7CF2"), C("#12121E"), C("#1A1A2B"), C("#222338"), C("#2E2F4A"), C("#D8DAEE"), C("#8B8FB0"), C("#0E0E17")),
        new ThemeScheme("forest",   "Forest (green)",        true,  C("#4CBB6C"), C("#121A14"), C("#18241B"), C("#1F2E23"), C("#2C4032"), C("#D5E2D8"), C("#8CA394"), C("#0D140F")),
        new ThemeScheme("amber",    "Amber (warm)",          true,  C("#E0A030"), C("#1C1712"), C("#251E17"), C("#2F261C"), C("#443627"), C("#E8DECF"), C("#A8977E"), C("#14100C")),
        new ThemeScheme("crimson",  "Crimson (red)",         true,  C("#E04858"), C("#1B1215"), C("#251A1E"), C("#2F2127"), C("#452E35"), C("#E6D9DC"), C("#A88F96"), C("#140D10")),
    };

    public static ThemeScheme Default => Schemes[0];

    /// <summary>Look a scheme up by its persisted key; unknown/null falls back to the default.</summary>
    public static ThemeScheme Find(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
            foreach (ThemeScheme s in Schemes)
                if (string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))
                    return s;
        return Default;
    }

    /// <summary>Apply the scheme with the given key (null → default) app-wide.</summary>
    /// <summary>Apply the ANSI palette choice ("classic" = MUSHclient, else modern). Affects
    /// MUD lines parsed after this call.</summary>
    public static void ApplyAnsiPalette(string? mode) =>
        Scrye.Core.Text.Rgb.AnsiPalette = string.Equals(mode, "classic", StringComparison.OrdinalIgnoreCase)
            ? Scrye.Core.Text.Rgb.AnsiPaletteMode.Classic
            : Scrye.Core.Text.Rgb.AnsiPaletteMode.Modern;

    public static void Apply(string? key)
    {
        if (Application.Current is not Application app) return;
        ThemeScheme s = Find(key);

        app.RequestedThemeVariant = s.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;

        IResourceDictionary r = app.Resources;

        // Fluent accent family — drives selection, toggles, focus rings, tab underline.
        r["SystemAccentColor"] = s.Accent;
        r["SystemAccentColorDark1"] = Mix(s.Accent, Colors.Black, 0.15);
        r["SystemAccentColorDark2"] = Mix(s.Accent, Colors.Black, 0.30);
        r["SystemAccentColorDark3"] = Mix(s.Accent, Colors.Black, 0.45);
        r["SystemAccentColorLight1"] = Mix(s.Accent, Colors.White, 0.15);
        r["SystemAccentColorLight2"] = Mix(s.Accent, Colors.White, 0.30);
        r["SystemAccentColorLight3"] = Mix(s.Accent, Colors.White, 0.45);

        // Scrye palette — everything in Theme.axaml / MainWindow.axaml uses DynamicResource.
        r["ScryeBg"] = new SolidColorBrush(s.Bg);
        r["ScryeOutputBg"] = new SolidColorBrush(OutputBg);     // constant: see class remarks
        r["ScryeOutputText"] = new SolidColorBrush(OutputText); // constant: terminal/input text
        r["ScryePanel"] = new SolidColorBrush(s.Panel);
        r["ScryePanelAlt"] = new SolidColorBrush(s.PanelAlt);
        r["ScryePanelGlass"] = new SolidColorBrush(Color.FromArgb(0xE6, s.Panel.R, s.Panel.G, s.Panel.B));
        r["ScryeInsetBg"] = new SolidColorBrush(s.InsetBg);
        r["ScryeLine"] = new SolidColorBrush(s.Line);
        r["ScryeText"] = new SolidColorBrush(s.Text);
        r["ScryeTextDim"] = new SolidColorBrush(s.TextDim);
        r["ScryeAccent"] = new SolidColorBrush(s.Accent);
    }

    private static Color C(string hex) => Color.Parse(hex);

    /// <summary>Linear blend of <paramref name="a"/> toward <paramref name="b"/>.</summary>
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
