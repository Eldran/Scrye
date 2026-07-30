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
/// </list>
/// </summary>
public sealed record WidgetSpec
{
    public string Type { get; init; } = "label";
    public string? Text { get; init; }
    public string? Bind { get; init; }
    public string? Value { get; init; }
    public string? Max { get; init; }
    public string? Color { get; init; }

    /// <summary>For <c>button</c> widgets: an opaque action id the host calls back with when
    /// clicked (the plugin runtime maps it to a Lua callback). Set by the runtime, not authors.</summary>
    public string? Action { get; init; }
}

/// <summary>
/// A declarative panel a plugin contributes via <c>scrye.addPanel</c>. The host turns
/// it into a HUD panel and keeps its bound widgets in sync with the state store
/// (Foundation D — the alternative to MUSHclient's imperative miniwindow drawing).
/// </summary>
public sealed record PanelSpec
{
    public string Title { get; init; } = "";
    public IReadOnlyList<WidgetSpec> Widgets { get; init; } = Array.Empty<WidgetSpec>();
}
