using Scrye.Core.Automation;
using Scrye.Core.Plugins;
using Scrye.Core.Session;
using Scrye.Core.Text;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// Binds <see cref="IPluginHost"/> to a live <see cref="MudSession"/>. Actions go
/// through the session's <see cref="IWorldActions"/> (so they run on the loop);
/// state reads/watches hit the session's <see cref="MudSession.GameState"/>; local
/// output is routed through the supplied print sink.
///
/// <para><b>Colour.</b> <see cref="Print"/> and <see cref="Capture"/> run their text through
/// <see cref="Markup"/>, so a plugin can colour individual runs the way MUSHclient's
/// ColourNote did (plugin API 1.2). Sinks that cannot show colour — <see cref="Log"/> and
/// <see cref="Notify"/> — strip the markup instead, so a plugin that colours a line never
/// leaks "@{accent}" into its log file or a desktop toast.</para>
/// </summary>
public sealed class SessionPluginHost : IPluginHost
{
    private readonly MudSession _session;
    private readonly IWorldActions _actions;
    private readonly Action<string, Line> _print;               // (pluginId, rendered line)
    private readonly Action<string, PanelSpec> _addPanel;       // (pluginId, panel) → App builds the HUD VM
    private readonly PluginDataStore? _data;                    // persistent scrye.store backing (optional)
    private readonly Func<string, Rgb?>? _resolveColour;        // theme-token lookup for markup
    private readonly Rgb _printFore;                            // default colour for plugin output

    /// <param name="print">Receives the rendered line. It arrives already coloured and already
    /// tagged with the plugin id, so the App only has to enqueue it.</param>
    /// <param name="resolveColour">Resolves a theme-token name to a colour for
    /// <see cref="Markup"/>. Null means hex literals still work but token names do not —
    /// fine for headless hosts, but the Avalonia host should pass its theme lookup.</param>
    /// <param name="printFore">Colour for uncoloured plugin output (the App's plugin green).</param>
    public SessionPluginHost(MudSession session, Action<string, Line> print, Action<string, PanelSpec> addPanel,
                             PluginDataStore? data = null, Func<string, Rgb?>? resolveColour = null,
                             Rgb? printFore = null)
    {
        _session = session;
        _actions = session;   // MudSession implements IWorldActions
        _print = print;
        _addPanel = addPanel;
        _data = data;
        _resolveColour = resolveColour;
        _printFore = printFore ?? Rgb.DefaultFore;
    }

    public void Send(string text) => _actions.Send(text);

    /// <summary>Echo a line to local output. The "[id] " tag is always the plugin colour; the
    /// rest of the line honours <see cref="Markup"/> and falls back to the same colour.</summary>
    public void Print(string pluginId, string text)
    {
        var runs = new List<StyledRun> { new($"[{pluginId}] ", _printFore, Rgb.DefaultBack, RunFlags.None) };
        foreach (StyledRun r in Markup.Parse(text, _resolveColour, _printFore)) runs.Add(r);
        _print(pluginId, new Line(runs, isPrompt: false, DateTimeOffset.UtcNow));
    }

    public string? GetVariable(string name) => _actions.GetVariable(name);
    public void SetVariable(string name, string value) => _actions.SetVariable(name, value);

    public string GetState(string path) => _session.GameState.Get(path).Text;

    public void SetState(string path, string value) =>
        _session.GameState.Set(path, Scrye.Core.State.StateValue.Str(value));

    public IDisposable WatchState(string path, Action<string, string> onChange) =>
        _session.GameState.Watch(path, (p, v) => onChange(p, v.Text));

    public void AddPanel(string pluginId, PanelSpec panel) => _addPanel(pluginId, panel);

    /// <summary>Route a line into a capture pane, honouring <see cref="Markup"/> — this is how
    /// the chat plugin gets its per-channel colours back.</summary>
    public void Capture(string pluginId, string pane, string text) =>
        _session.RoutePane(pane, Markup.ToLine(text, _resolveColour));

    public void PlaySound(string sound) => _session.RequestSound(sound);

    // Toasts are plain text (and reach the phone as a push), so markup is stripped, not rendered.
    public void Notify(string pluginId, string text) =>
        _session.RequestNotify(Line.FromText($"[{pluginId}] {Markup.Strip(text)}"));

    // ---- per-plugin log file (scrye.log) --------------------------------------
    // Appends to %APPDATA%/Scrye/logs/plugins/<id>.log. Best-effort: a logging
    // failure must never take down a plugin, so all IO errors are swallowed.
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Scrye", "logs", "plugins");

    public void Log(string pluginId, string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogDir);
            string safe = string.Join("_", pluginId.Split(System.IO.Path.GetInvalidFileNameChars()));
            // strip markup: a log file is plain text, and "@{accent}" in it helps nobody
            System.IO.File.AppendAllText(System.IO.Path.Combine(LogDir, safe + ".log"),
                Markup.Strip(text) + Environment.NewLine);
        }
        catch { /* logging is best-effort; never surface IO errors to the plugin */ }
    }

    // ---- persistent per-plugin storage (scrye.store) --------------------------

    public string? StoreGet(string pluginId, string key) => _data?.Get(pluginId, key);
    public void StoreSet(string pluginId, string key, string value) => _data?.Set(pluginId, key, value);
    public void StoreDelete(string pluginId, string key) => _data?.Delete(pluginId, key);
    public string[] StoreKeys(string pluginId) => _data?.Keys(pluginId) ?? Array.Empty<string>();
}
