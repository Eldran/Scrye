namespace Scrye.Core.Plugins;

/// <summary>
/// A declarative UI widget in a plugin panel. Data only — no drawing. The host
/// renders it and binds it to game state. Which fields matter depends on
/// <see cref="Type"/>:
/// <list type="bullet">
/// <item><c>label</c> — static <see cref="Text"/>, or bound to <see cref="Bind"/>.</item>
/// <item><c>value</c> — <see cref="Text"/> as a prefix + the live value at <see cref="Bind"/>.</item>
/// <item><c>progress</c> — a bar; <see cref="Value"/> and <see cref="Max"/> are state paths
/// (or numeric literals for <see cref="Max"/>), <see cref="Text"/> is the caption/label.</item>
/// <item><c>gauge</c> — a labelled bar with the current/max readout inside and a
/// fill colour that shifts with the percentage (healthy → warning → critical).</item>
/// <item><c>text</c> — a multi-line monospace block bound to <see cref="Bind"/>
/// (plugins compose reports into a state path and bind it here).</item>
/// <item><c>colorgrid</c> — a grid of coloured cells: the state value at
/// <see cref="Bind"/> is newline-separated rows of characters, and
/// <see cref="Palette"/> maps each character to a "#RRGGBB" colour (maps, charts).</item>
/// <item><c>button</c> — a clickable button firing the plugin callback in <see cref="Action"/>.</item>
/// <item><c>colorgrid</c> cells are clickable when the widget sets an <c>onClick</c> callback;
/// the host invokes it with the clicked cell's (col, row, char).</item>
/// </list>
/// </summary>
public sealed record WidgetSpec
{
    public string Type { get; init; } = "label";
    public string? Text { get; init; }
    public string? Bind { get; init; }
    public string? Value { get; init; }
    public string? Max { get; init; }
    /// <summary>Custom "#RRGGBB" colour. Applies to <c>label</c>/<c>value</c>/<c>text</c>
    /// (foreground) and <c>progress</c>/<c>gauge</c> (bar fill, overriding the theme accent /
    /// the gauge's health gradient). Null = follow the theme / the panel's default foreground.</summary>
    public string? Color { get; init; }

    /// <summary>For <c>colorgrid</c>: character → "#RRGGBB" cell colour.</summary>
    public IReadOnlyDictionary<string, string>? Palette { get; init; }

    /// <summary>For <c>button</c> widgets: an opaque action id the host calls back with when
    /// clicked (the plugin runtime maps it to a Lua callback). Set by the runtime, not authors.</summary>
    public string? Action { get; init; }
}

/// <summary>One tab in a tabbed panel: a title and its widgets.</summary>
public sealed record PanelTabSpec
{
    public string Title { get; init; } = "";
    public IReadOnlyList<WidgetSpec> Widgets { get; init; } = Array.Empty<WidgetSpec>();
}

/// <summary>
/// A declarative panel a plugin contributes via <c>scrye.addPanel</c>. The host turns
/// it into a HUD panel and keeps its bound widgets in sync with the state store
/// (Foundation D — the alternative to MUSHclient's imperative miniwindow drawing).
/// Either a flat <see cref="Widgets"/> list, or a set of <see cref="Tabs"/> (when
/// non-empty the panel renders as a tab strip and <see cref="Widgets"/> is ignored).
/// </summary>
public sealed record PanelSpec
{
    public string Title { get; init; } = "";
    public IReadOnlyList<WidgetSpec> Widgets { get; init; } = Array.Empty<WidgetSpec>();
    public IReadOnlyList<PanelTabSpec> Tabs { get; init; } = Array.Empty<PanelTabSpec>();

    /// <summary>Panel width in pixels; 0 = the HUD default (narrow).</summary>
    public double Width { get; init; }

    /// <summary>Optional panel background "#RRGGBB" — overrides the theme's panel colour so a
    /// plugin can give its pane a distinct look. Null = follow the active colour scheme.</summary>
    public string? Background { get; init; }

    /// <summary>Optional accent "#RRGGBB" for the panel's border and title. Null = theme accent.</summary>
    public string? Accent { get; init; }

    /// <summary>Optional default text colour "#RRGGBB" for label/value/text widgets in this panel
    /// that don't set their own <see cref="WidgetSpec.Color"/>. Null = theme text colour.</summary>
    public string? Foreground { get; init; }
}
