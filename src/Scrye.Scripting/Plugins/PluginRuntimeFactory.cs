using Scrye.Core.Plugins;
using Scrye.Scripting.Lua;
using Scrye.Scripting.Wasm;

namespace Scrye.Scripting.Plugins;

/// <summary>
/// The one place a manifest's <c>lang</c> becomes a concrete <see cref="IPluginRuntime"/> —
/// extracted from <c>PluginManager.LoadOne</c>'s ternary so runtimes can be added (native
/// Lua landed here; Wasm is next) without touching the manager.
///
/// <para>Langs: <c>"lua"</c> (native Lua 5.4 via KeraLua), <c>"js"</c> (Jint), and
/// <c>"wasm"</c> (a compiled WebAssembly module speaking scrye-wasm-abi v1 —
/// docs/scrye-wasm-abi.md — on Wasmtime; the only runtime whose manifest permissions are
/// ENFORCED). Anything else falls through to Lua, matching the original manager behaviour.
/// <c>"lua-native"</c>/<c>"lua-ms"</c> were soak-period pins during the MoonSharp→KeraLua
/// migration; MoonSharp is gone, so both fall through to the one Lua runtime.</para>
/// </summary>
public static class PluginRuntimeFactory
{
    public static IPluginRuntime Create(PluginDescriptor descriptor, IPluginHost host,
                                        PluginDiagnostics? diagnostics = null)
    {
        string lang = descriptor.Manifest.Lang;
        if (lang.Equals("js", StringComparison.OrdinalIgnoreCase))
            return new JsPluginRuntime(descriptor, host, diagnostics);
        if (lang.Equals("wasm", StringComparison.OrdinalIgnoreCase))
            return new WasmPluginRuntime(descriptor, host, diagnostics);
        return new KeraLuaPluginRuntime(descriptor, host, diagnostics);
    }
}
