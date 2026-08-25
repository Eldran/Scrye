namespace Scrye.Core.Plugins;

/// <summary>
/// The capabilities the host (a live session) offers a plugin. Implemented by a
/// session bridge; consumed by the Lua runtime, which wraps these as the <c>scrye.*</c>
/// script API. Deliberately engine-free (lives in the contracts assembly) so the API surface stays
/// language-agnostic — a future JavaScript host binds the same interface.
///
/// Values are exchanged as strings (Lua-friendly; numbers convert with <c>tonumber</c>).
/// Everything runs on the session loop thread, matching trigger/alias script execution.
/// </summary>
public interface IPluginHost
{
    /// <summary>Send a line to the MUD.</summary>
    void Send(string text);

    /// <summary>Echo a line to local output, tagged with the plugin id.</summary>
    void Print(string pluginId, string text);

    string? GetVariable(string name);
    void SetVariable(string name, string value);

    /// <summary>Current value of a state path (e.g. "character.health.current"), or "" if unset.</summary>
    string GetState(string path);

    /// <summary>Publish a value into the state tree (plugins compose derived state —
    /// e.g. a rendered map grid — under <c>plugin.&lt;id&gt;.*</c> and bind widgets to it).
    /// Runs on the session loop like everything else, so it never races the store.</summary>
    void SetState(string path, string value);

    /// <summary>Watch a state path/subtree; callback gets (changedPath, valueText).
    /// Returns an <see cref="IDisposable"/> the runtime disposes when the plugin unloads.</summary>
    IDisposable WatchState(string path, Action<string, string> onChange);

    /// <summary>Contribute a declarative HUD panel. The host renders it and keeps its
    /// bound widgets in sync with state (Foundation D).</summary>
    void AddPanel(string pluginId, PanelSpec panel);

    /// <summary>Route a text line into a named capture pane (parity with trigger
    /// CapturePane). Default no-op so headless hosts need not care.</summary>
    void Capture(string pluginId, string pane, string text) { }

    /// <summary>Play a sound ("beep", path, or sounds-folder file). Default no-op.</summary>
    void PlaySound(string sound) { }

    /// <summary>Raise a notification toast tagged with the plugin id. Default no-op.</summary>
    void Notify(string pluginId, string text) { }

    /// <summary>Append a line to the plugin's own log file (real hosts write
    /// <c>%APPDATA%/Scrye/logs/plugins/&lt;id&gt;.log</c>). Sandboxed scripts can't touch the
    /// filesystem, so this is the sanctioned way for a plugin to keep a durable log.
    /// Default no-op so headless hosts and tests need not care.</summary>
    void Log(string pluginId, string text) { }

    // ---- persistent per-plugin storage (scrye.store) --------------------------
    // Backed by PluginDataStore in real hosts; defaults are no-ops so headless
    // hosts and tests need not care. Data survives sessions and app restarts.

    /// <summary>Stored value for a key, or null if unset. Default: nothing stored.</summary>
    string? StoreGet(string pluginId, string key) => null;

    /// <summary>Persist a key/value pair for the plugin. Default no-op.</summary>
    void StoreSet(string pluginId, string key, string value) { }

    /// <summary>Remove a stored key. Default no-op.</summary>
    void StoreDelete(string pluginId, string key) { }

    /// <summary>All stored keys for the plugin. Default: empty.</summary>
    string[] StoreKeys(string pluginId) => Array.Empty<string>();

    /// <summary>Persist several key/value pairs in one operation (the <c>scrye.store.setMany</c>
    /// backing, API 1.6). Real hosts write the store file once for the whole batch — the reason
    /// this exists; a mapper saving 40 area keys should cost one disk write, not 40. The default
    /// falls back to per-key <see cref="StoreSet"/> so existing hosts stay correct.</summary>
    void StoreSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        foreach (KeyValuePair<string, string> kv in values) StoreSet(pluginId, kv.Key, kv.Value);
    }

    // ---- shared per-MUD storage (scrye.shared, API 1.14) ----------------------
    // The same key/value surface as scrye.store, but scoped to the MUD rather than the
    // profile: every character logged into the same world reads and writes the same file.
    // This is where world-truth belongs (a mapper's rooms - the MUD's map is the same for
    // everyone) while per-character truth (the chaos-sea bot's sea, which is generated per
    // character) stays in scrye.store. Opt-in per key, decided by the plugin author.
    // Defaults are no-ops so headless hosts and tests need not care; a script must treat a
    // missing binding as "no shared store on this host" and fall back to scrye.store.

    /// <summary>Shared stored value for a key, or null if unset. Default: nothing stored.</summary>
    string? SharedGet(string pluginId, string key) => null;

    /// <summary>Persist a key/value pair in the MUD-shared store. Default no-op.</summary>
    void SharedSet(string pluginId, string key, string value) { }

    /// <summary>Remove a shared key. Default no-op.</summary>
    void SharedDelete(string pluginId, string key) { }

    /// <summary>All shared keys for the plugin. Default: empty.</summary>
    string[] SharedKeys(string pluginId) => Array.Empty<string>();

    /// <summary>Batched shared write - one file save for the whole batch, like
    /// <see cref="StoreSetMany"/>. The default falls back to per-key <see cref="SharedSet"/>.</summary>
    void SharedSetMany(string pluginId, IReadOnlyDictionary<string, string> values)
    {
        foreach (KeyValuePair<string, string> kv in values) SharedSet(pluginId, kv.Key, kv.Value);
    }

    /// <summary>Broadcast an inter-plugin event (the <c>scrye.emit</c> backing, API 1.6). The
    /// host fans it out to every loaded plugin's <c>scrye.on(name, fn)</c> handlers — including
    /// the sender's own. Default no-op so headless hosts and tests need not care.</summary>
    void EmitEvent(string sourceId, string name, string data) { }
}
