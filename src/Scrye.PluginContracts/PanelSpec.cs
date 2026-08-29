namespace Scrye.Core.Plugins;

/// <summary>One entry of a plugin-provided context menu (API 1.18). A right-click callback
/// (<c>onRightClick</c> on a colorgrid, <c>onRowMenu</c> on a list/table) may RETURN a list
/// of these — <c>{ { "Walk there", "mapg go 42" }, { "-" } }</c> in script — and the host
/// shows them as a native menu at the pointer. <see cref="Command"/> runs exactly as if the
/// user typed it, so the plugin's own aliases get first refusal — the same philosophy as
/// <c>click=</c> markup, which is why an entry carries a command string and never a callback.
/// A label of <c>"-"</c> with no command is a separator. A callback that returns nothing
/// keeps its pre-1.18 act-directly behaviour; hosts that cannot show menus (the companion)
/// ignore the return entirely.</summary>
public sealed record MenuEntry(string Label, string? Command)
{
    /// <summary>True when this entry draws as a separator line rather than an item.</summary>
    public bool IsSeparator => Label == "-" && string.IsNullOrEmpty(Command);
}

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
/// <item><c>row</c> — a horizontal container (API 1.8): <see cref="Children"/> holds ordinary
/// widget specs laid out side by side, each at its measured width. In script the children come
/// from a <c>widgets</c> list on the row. The escape hatch from the panel's vertical stack —
/// a chart with its notes beside it rather than below.</item>
/// <item><c>colorgrid</c> cells are clickable when the widget sets an <c>onClick</c> callback;
/// the host invokes it with the clicked cell's (col, row, char).</item>
/// <item><c>list</c> — a dynamic list of rows from <see cref="Bind"/>: one row per line, each
/// optionally <c>label &lt;sep&gt; value</c> with the value right-aligned and dimmed. The row
/// count follows the bound value, so unlike the fixed widget set a list grows and shrinks at
/// runtime.</item>
/// <item><c>table</c> — the same rows split into columns by <see cref="Separator"/>, with
/// optional <see cref="Columns"/> headers and per-column <see cref="Align"/>. Columns are
/// measured to their widest cell.</item>
/// </list>
///
/// <para><b>Row clicks (API 1.15).</b> A <c>list</c> or <c>table</c> whose script sets an
/// <c>onRowClick</c> function becomes row-clickable: the callback id lands in
/// <see cref="Action"/> and the host fires it through the choice path with the clicked row's
/// first cell and its 1-based index — the same (label, index) shape a bound buttonrow
/// reports, because a clickable row IS a choice from a dynamic set. The header row is
/// structure, not content, and never fires it. Hosts that render panels as plain text (the
/// companion) ignore it, exactly as they ignore colorgrid clicks.</para>
///
/// <para><b>Colours.</b> <see cref="Color"/> and the values in <see cref="Palette"/> accept
/// either a <c>"#RRGGBB"</c> literal or one of the semantic names in <see cref="ThemeToken"/>
/// (<c>"accent"</c>, <c>"warning"</c>, <c>"dim"</c>, …). Prefer the token — a literal hard-codes
/// one colour scheme into a client that ships six, and the mobile companion resolves the same
/// token against its own palette. Tokens resolve when the panel is built, so a scheme change
/// re-colours plugin panels after a plugin reload.</para>
///
/// <para><b>On dynamic content generally.</b> <c>list</c> and <c>table</c> exist because the
/// widget <i>set</i> is fixed once a panel is built; before them, the only way to show a
/// variable-length collection was to compose it into one <c>text</c> blob or a
/// <c>colorgrid</c> — which is why plugins ended up doing column alignment in Lua. If you are
/// reaching for <c>string.format</c> and padding, you probably want <c>table</c>.</para>
/// </summary>
public sealed record WidgetSpec
{
    public string Type { get; init; } = "label";
    public string? Text { get; init; }
    public string? Bind { get; init; }
    public string? Value { get; init; }
    public string? Max { get; init; }
    /// <summary>Custom colour — a "#RRGGBB" literal or a <see cref="ThemeToken"/> name. Applies to
    /// <c>label</c>/<c>value</c>/<c>text</c>/<c>list</c>/<c>table</c> (foreground) and
    /// <c>progress</c>/<c>gauge</c> (bar fill, overriding the theme accent / the gauge's health
    /// gradient). Null = follow the theme / the panel's default foreground.</summary>
    public string? Color { get; init; }

    /// <summary>For a <c>gauge</c>: dim the bar toward dark as the value drops (brightness scales
    /// with the fill ratio) instead of the cyan→amber→red gradient. Uses <see cref="Color"/> as the
    /// base hue (default green when unset).</summary>
    public bool Dim { get; init; }

    /// <summary>For <c>colorgrid</c>: character → cell colour ("#RRGGBB" or a
    /// <see cref="ThemeToken"/> name).</summary>
    public IReadOnlyDictionary<string, string>? Palette { get; init; }

    /// <summary>
    /// <c>colorgrid</c> only (API 1.8): character → micro-icon name. An iconed cell draws a
    /// muted tile of its palette colour with a tiny vector glyph on top in a lightened shade
    /// of the same colour — terrain that looks like terrain. The glyph vocabulary (host-drawn,
    /// no image assets): <c>water dashes grass hill tree pine mountain house tower gate ruin
    /// star person ship anchor flag bolt crown hammer cross dot</c>. Icons take precedence over
    /// <see cref="Labels"/> letters for the same character; cells too small for a glyph
    /// (&lt; 8 px) fall back to the plain colour tile and then the letter rules. Unknown names
    /// render as plain tiles, and text-only hosts (the companion) ignore this map entirely —
    /// the character grid remains the fallback by design.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Icons { get; init; }

    /// <summary>
    /// <c>colorgrid</c> only (API 1.17): character → image file for that tile. The plugin
    /// declares paths relative to its own folder (<c>images = { T = "tiles/tower.png" }</c>);
    /// the runtime resolves them and DROPS any that land outside the plugin's folder, so the
    /// spec that reaches a host only ever carries absolute paths into the declaring plugin's
    /// own files. An imaged cell draws the bitmap as the tile — nearest-neighbour scaled, so
    /// pixel art keeps its fat pixels — and takes precedence over <see cref="Icons"/> and
    /// <see cref="Labels"/> for that character. A cell too small for art (&lt; 8 px), or a
    /// file that is missing or unreadable, falls back to the plain palette tile and then the
    /// letter rules — a lost PNG costs the art, never the map. Text-only hosts ignore this
    /// map entirely; the character grid remains the fallback by design.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Images { get; init; }

    /// <summary><c>colorgrid</c> only (API 1.8): the LARGEST cell size in pixels this grid may
    /// use — 0 means the compact default (12). Cells still shrink to fit the panel width; this
    /// only raises the ceiling, for charts whose icons deserve room (the viking sea chart uses
    /// 24). Hosts clamp hostile values (3–64); text-rendering hosts ignore it.</summary>
    public double Cell { get; init; }

    /// <summary>For <c>table</c>: optional header labels. When set, a header row is drawn and the
    /// column count is taken from here; extra fields in a data row are ignored and missing ones
    /// render blank. When unset the table is headerless and sizes to the widest row.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>For <c>list</c>/<c>table</c>: the field separator inside each bound line.
    /// Defaults to a tab, which is what a plugin composing rows in script will reach for anyway
    /// and which cannot collide with display text the way "|" or "," can.</summary>
    public string? Separator { get; init; }

    /// <summary>For <c>table</c>: per-column alignment as one character each — <c>'l'</c> left,
    /// <c>'r'</c> right, <c>'c'</c> centre (e.g. <c>"llr"</c>). Shorter than the column count
    /// leaves the rest left-aligned. Right-aligning numeric columns is the whole reason this
    /// exists; without it a table of quantities reads worse than the text blob it replaced.</summary>
    public string? Align { get; init; }

    /// <summary>For <c>button</c> widgets: an opaque action id the host calls back with when
    /// clicked (the plugin runtime maps it to a Lua callback). Set by the runtime, not authors.
    /// For <c>list</c>/<c>table</c> (API 1.15) it carries the <c>onRowClick</c> callback id,
    /// fired through the choice path with (first cell, 1-based row index).</summary>
    public string? Action { get; init; }

    /// <summary>
    /// <c>colorgrid</c> only: the characters that should be drawn as a letter on top of their
    /// cell colour, e.g. <c>"SXHWTI&gt;*B"</c>. A colour tile says "something is here"; the letter
    /// says what, which is how the MUSHclient charts read. Everything not listed stays a plain
    /// tile, so terrain still reads as terrain rather than a wall of characters.
    ///
    /// <para>Null or empty means no letters. Letters are skipped automatically when the cells
    /// are too small to fit one.</para>
    /// </summary>
    public string? Labels { get; init; }

    /// <summary>For a <c>buttonrow</c> widget: the child button specs, rendered side by side as
    /// equal-width columns. Each child is an ordinary button spec (text + action).</summary>
    public IReadOnlyList<WidgetSpec>? Children { get; init; }

    /// <summary>
    /// <c>colorgrid</c> only (API 1.7): render the grid as a <b>weave</b> — cells at even
    /// column/row indices draw as full-size tiles, and the odd cells between them are narrow,
    /// with the connector characters <c>-</c> <c>|</c> <c>/</c> <c>\</c> <c>x</c> drawn as thin
    /// lines in their palette colour (<c>x</c> = both diagonals crossing). This is how a map
    /// shows the exits BETWEEN rooms: double the character grid (rooms on even cells, exits on
    /// the odd cells they share) and the weave renders rooms at nearly full tile size with the
    /// connections visible. Click/hover coordinates report the raw doubled (col, row); the
    /// plugin halves the even ones to get back to its own grid. Hosts that render characters
    /// as text (the companion) ignore this flag — the same connector characters read as an
    /// ASCII map there, which is the fallback by design.
    /// </summary>
    public bool Weave { get; init; }

    /// <summary>
    /// <c>colorgrid</c> only (API 1.6): an opaque action id the host calls back with when the
    /// pointer moves onto a different cell — <c>(col, row, char)</c>, and <c>(-1, -1, "")</c>
    /// once when the pointer leaves the grid, so a plugin can clear whatever it was showing.
    /// Set by the runtime from the widget's <c>onHover</c> function, not by authors. Hover is a
    /// pointer affordance: touch devices (the mobile companion) never fire it, so it must only
    /// ever *enrich* the grid — a room name preview, a coordinate readout — never gate anything
    /// <c>onClick</c> can't reach.
    /// </summary>
    public string? HoverAction { get; init; }

    /// <summary>
    /// <c>colorgrid</c>, <c>button</c> and <c>buttonrow</c> (API 1.9): an opaque action id the
    /// host calls back with on a <b>secondary</b> activation — right-click on the desktop, a
    /// long-press on the mobile companion. Set by the runtime from the widget's
    /// <c>onRightClick</c> function, not by authors.
    ///
    /// <para>This is a second action, not a variant of the first: a grid fires it with the same
    /// <c>(col, row, ch)</c> as <see cref="Action"/>, a button with no arguments, and
    /// <see cref="Action"/> does not also fire. Because touch reaches it through long-press,
    /// it may gate real behaviour — the restriction that binds <see cref="HoverAction"/> to
    /// enrichment only does not apply here.</para>
    /// </summary>
    public string? ContextAction { get; init; }
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

    /// <summary>Optional panel background ("#RRGGBB" or a <see cref="ThemeToken"/> name) — overrides
    /// the theme's panel colour so a plugin can give its pane a distinct look. Null = follow the
    /// active colour scheme.</summary>
    public string? Background { get; init; }

    /// <summary>Optional accent for the panel's border and title ("#RRGGBB" or a
    /// <see cref="ThemeToken"/> name). Null = theme accent.</summary>
    public string? Accent { get; init; }

    /// <summary>Optional default text colour ("#RRGGBB" or a <see cref="ThemeToken"/> name) for
    /// text-bearing widgets in this panel that don't set their own <see cref="WidgetSpec.Color"/>.
    /// Null = theme text colour.</summary>
    public string? Foreground { get; init; }
}
