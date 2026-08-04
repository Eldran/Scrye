namespace Scrye.Core.Plugins;

/// <summary>
/// The semantic colour names a plugin may use anywhere a <c>#RRGGBB</c> literal is accepted
/// (<see cref="WidgetSpec.Color"/>, <see cref="PanelSpec.Accent"/>/<see cref="PanelSpec.Background"/>/
/// <see cref="PanelSpec.Foreground"/>, and <see cref="WidgetSpec.Palette"/> values).
///
/// <para><b>Why this exists.</b> A plugin that writes <c>color = "#202020"</c> has hard-coded a
/// dark-theme assumption into a client with six colour schemes and a light one. The token names
/// below are resolved by whichever host is rendering — the Avalonia HUD looks them up in the
/// active <c>ThemeScheme</c>, the mobile companion in its own palette — so a plugin says what a
/// colour <i>means</i> and the host decides what it looks like.</para>
///
/// <para><b>These names are API.</b> They appear in published plugins the moment anyone uses
/// them; renaming or removing one is a breaking change and needs a major
/// <see cref="ScryeApi"/> bump. Adding a name is a minor bump.</para>
///
/// <para>Resolution is by prefix: a value starting with '#' is a literal, anything else is
/// looked up here, and an unknown name resolves to nothing (so the widget falls back to the
/// theme default rather than rendering invisibly).</para>
/// </summary>
public static class ThemeToken
{
    /// <summary>The scheme's accent — the colour the client already uses for headings and focus.</summary>
    public const string Accent = "accent";
    /// <summary>Primary body text.</summary>
    public const string Text = "text";
    /// <summary>Secondary/less important text. The usual choice for captions and units.</summary>
    public const string Dim = "dim";
    /// <summary>The window background.</summary>
    public const string Bg = "bg";
    /// <summary>Panel surface — what a HUD panel sits on.</summary>
    public const string Panel = "panel";
    /// <summary>A slightly raised panel surface, for rows and nested boxes.</summary>
    public const string PanelAlt = "panelalt";
    /// <summary>A recessed surface, for inputs and wells.</summary>
    public const string Inset = "inset";
    /// <summary>Borders and separators.</summary>
    public const string Line = "line";
    /// <summary>Good/healthy/complete.</summary>
    public const string Success = "success";
    /// <summary>Caution — worth a look but not broken.</summary>
    public const string Warning = "warning";
    /// <summary>Bad/failed/critical.</summary>
    public const string Error = "error";
    /// <summary>Neutral informational highlight.</summary>
    public const string Info = "info";

    /// <summary>Every token name, for docs, validation and the plugin scaffold comment.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Accent, Text, Dim, Bg, Panel, PanelAlt, Inset, Line, Success, Warning, Error, Info,
    };

    /// <summary>True when <paramref name="value"/> names a known token (case-insensitive).
    /// A '#' literal is never a token.</summary>
    public static bool IsToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value[0] != '#' &&
        All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="value"/> is a plausible colour at all — a '#RRGGBB' literal or a
    /// known token. Used to warn an author about <c>color = "gren"</c> instead of silently
    /// rendering theme-default and leaving them to wonder why.
    /// </summary>
    public static bool IsColour(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (IsToken(value) || (value.Length == 7 && value[0] == '#' &&
                            uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _)));

    /// <summary>The canonical lower-case form of a token, or null if it isn't one.</summary>
    public static string? Normalize(string? value) =>
        IsToken(value) ? value!.Trim().ToLowerInvariant() : null;
}
