# Plan: WebAssembly plugins (Rust-first), as an optional third runtime

*Scrye plugin system extension — planning document, 2026-08-06.*

> **STATUS (2026-08-07): W0–W2 IMPLEMENTED.** The ABI is specified (`scrye-wasm-abi.md`),
> `WasmPluginRuntime` is live behind the factory as `lang: "wasm"` with epoch deadlines,
> the 64 MB memory cap, and permission-gated import linking (enforced permissions), all
> validated against C-built fixture modules covering the full IPluginRuntime surface
> (`tests/fixtures/wasm`, `WasmPluginTests`). The Rust SDK (`sdk/rust/scrye-plugin`) and
> the hp-watch example exist as cargo-checked source; first wasm32 build happens on a
> machine with the rustup target. Deliberate deviations from this plan: no experimental
> settings flag (`lang: "wasm"` is already opt-in per plugin), and no compilation cache
> (measured cold compile is ~18 ms — revisit only if real plugins get big). W3's remaining
> item is exactly that cache decision; W4's is publishing the crate/template.

**Decision already made:** build on **Wasmtime** via the official Bytecode Alliance .NET bindings (NuGet package `Wasmtime`, currently v44.x, tracking upstream Wasmtime releases). Extism was considered and passed over: it would save some host-function plumbing, but Scrye already has exactly the host-abstraction pattern Extism provides (`IPluginHost`), and a direct ABI keeps the dependency surface to one well-governed project.

---

## 1. What this is

A third value for the manifest's `lang` field — `"wasm"` — whose entry is a compiled WebAssembly module instead of a script:

```json
{
  "id": "3s-pathfinder",
  "name": "Pathfinder",
  "version": "1.0.0",
  "lang": "wasm",
  "entry": "main.wasm",
  "permissions": ["send", "state", "store"]
}
```

Rust is the first-class authoring language (via a published `scrye-plugin` crate), but the ABI is language-neutral — anything that compiles to a core wasm module (Zig, C, Go/TinyGo, AssemblyScript) can target it.

**Why bother, when Lua and JS exist:**

- **Performance.** A mapper's pathfinding, market-data crunching, or log analytics run at near-native speed. This is the tier for plugins where the Lua runtimes are the bottleneck even after the KeraLua migration.
- **A sandbox that is actually a boundary.** Lua/JS sandboxing is "we didn't bind the dangerous stuff." Wasm sandboxing is a memory-isolated VM with metered execution. Notably, this makes the manifest's `permissions` field — today *"declarations, not enforcement"* per `PluginPermissions` — **enforceable** for wasm plugins: the host simply does not link the imports a plugin didn't declare (§4.4).
- **Runaway protection.** Wasmtime's epoch interruption gives hard per-dispatch deadlines. A plugin that infinite-loops gets cleanly trapped and flows into the existing `PluginDiagnostics` quarantine, and cannot freeze the session loop.
- **Distributable binaries.** Authors who don't want to ship readable source can ship a module.

**Non-goals for v1:** no WASI (no clocks/random/fs from the guest — everything comes through `scrye` imports, same philosophy as the Lua sandbox); no component model / wit-bindgen (revisit once the .NET side of components matures — the ABI below is versioned so we can migrate); no threading; no wasm access to widget rendering beyond the same `PanelSpec` JSON every other runtime uses.

## 2. Where it plugs in

`IPluginRuntime` is already the language-agnostic contract (Lua and JS both implement it; everything runs on the session loop thread, single-threaded — which matches Wasmtime's `Store` threading model exactly). The work is one new implementation plus factory registration:

```
Scrye.Scripting/
  Wasm/
    WasmPluginRuntime.cs    IPluginRuntime over a Wasmtime Instance
    WasmAbi.cs              import bindings + memory/string helpers
    WasmLimits.cs           epoch deadlines, memory caps, fuel config
```

`PluginRuntimeFactory` (created in the KeraLua migration, Phase 2 of that plan) gains a `lang == "wasm"` branch. `PluginManager`, diagnostics, quarantine, panels, store, timers: all unchanged. **Sequencing dependency:** do the factory refactor first; otherwise this plan starts by touching `PluginManager.LoadOne` itself.

One shared `Engine` (module compilation, epoch ticking) for the app; one `Store` + `Instance` per plugin (mirrors one `lua_State`/`Script` per plugin — full isolation, cheap disposal).

## 3. The ABI ("scrye-abi" v1)

The interesting design problem: Lua plugins register *closures*; a wasm module exports *named functions*. The bridge is a hook-id protocol — the guest registers interest and gets back an integer id; the host later dispatches to one generic export with that id. JSON is the payload envelope everywhere (UTF-8 in guest memory), reusing the same shapes the companion protocol already serializes (`PanelSpec` etc.).

### Guest exports (the host calls these)

```
scrye_abi_version() -> i32          // returns 1; checked before anything else
scrye_alloc(len: i32) -> ptr        // guest allocator for host→guest strings
scrye_free(ptr: i32, len: i32)
scrye_init()                        // entry point: register hooks, build panels
                                    // (equivalent of executing main.lua's body)
scrye_hook(hook_id: i32, ptr: i32, len: i32) -> i64
                                    // generic dispatch: payload JSON in,
                                    // packed (ptr,len) JSON out (0 = no result)
```

### Host imports (module `"scrye"` — the `scrye.*` API, one function per capability)

Strings pass as `(ptr, len)` into guest memory; returns that carry strings go through `scrye_alloc`.

- Core: `print`, `send`, `log`, `notify`, `sound`, `capture`
- State/vars: `get_state`, `set_state`, `get_variable`, `set_variable`, `watch_state(path) -> hook_id`
- Store: `store_get`, `store_set`, `store_set_many(json)`, `store_delete`, `store_keys() -> json`
- Events: `emit(name, data)`
- Registration (all return `hook_id`): `on_line`, `on_channel(filter)`, `on_gmcp(filter)`, `on_connect`, `on_disconnect`, `on_prompt`, `on_idle`, `on_command`, `on_event(name)`, `after(secs) -> hook_id`, `every(secs) -> hook_id`, `cancel(hook_id)`
- Rules: `add_trigger(json) -> hook_id?`, `add_alias(json) -> hook_id?` — same `{pattern, regex, ignoreCase, send}` shape; a rule with a `run` callback passes `"run": true` and receives wildcards through `scrye_hook`
- Panels: `add_panel(json)` — widget callbacks (`action`/`onClick`/`onSubmit`/`onHover`) are hook_ids the guest obtained from `register_action() -> hook_id` and embedded in the JSON where Lua embeds functions
- Data files: `get_data() -> json` (the manifest-declared `scrye.data`, serialized once at init)

### Dispatch payloads

`scrye_hook` payloads are small JSON objects tagged by what the id was registered as, e.g. `{"line": "..."}` → returns `{"gag": true}` or `{"rewrite": "..."}`; `{"channel": "Party", "message": "..."}`; `{"col": 3, "row": 4, "ch": "#"}` for cell actions; `{"wildcards": [...]}` for rule runs. Timer and lifecycle hooks get `{}`. The mapping from `IPluginRuntime` methods (`ProcessLine`, `DispatchGmcp`, `InvokeCellAction`, `InvokeChoice`, `InvokeSubmit`, …) to dispatches is 1:1 and mechanical; hook bookkeeping (which ids are line hooks, in what order) lives host-side in `WasmPluginRuntime`, mirroring `LuaPluginRuntime`'s `_lineHooks` et al.

**Versioning:** the import module is `"scrye"` with `scrye_abi_version` at 1. Additive growth = new imports (old modules don't import them, keep working — the wasm analogue of the additive-minor rule in `ScryeApi`). Breaking change = `scrye_abi_version` 2. The existing `requires.scryeApi` manifest gate applies as-is on top, since the *semantic* API (state paths, widget vocabulary, JSON shapes) is shared across all three runtimes.

**JSON-envelope cost note:** per-line dispatch serializes one small object. If profiling ever shows this matters for a hot plugin, ABI v1 reserves the option of a raw fast-path for `on_line` only (`(ptr,len)` string in, tag-byte out). Don't build it speculatively.

## 4. Host-side semantics

### 4.1 Loading

`Load()` = compile module (cached — see 4.5) → check `scrye_abi_version` → link only permitted imports → instantiate → call `scrye_init()` under a deadline. Any trap/mismatch throws, and `PluginManager.LoadOne` already reports-and-skips throwing plugins.

### 4.2 Execution limits (the quarantine story, upgraded)

- **Epoch interruption:** a per-dispatch deadline (default ~50 ms; generous — Lua plugins get no limit at all today). The app ticks the engine epoch from a timer; a guest that exceeds the deadline traps, the trap becomes `PluginDiagnostics.RecordFailure`, and repeated offenders quarantine exactly like a repeatedly-erroring Lua plugin. Epoch beats fuel here: near-zero overhead on the `ProcessLine` hot path.
- **Memory cap:** `StoreLimits` (default 64 MB, maybe manifest-raisable with user consent surfaced in the plugins manager). Exceeding it traps.
- **Traps are errors, not crashes:** every host→guest call goes through one `SafeDispatch` helper that catches `TrapException`/`WasmtimeException` and routes to the existing `Print` + diagnostics path — the wasm twin of `LuaPluginRuntime.Safe`.

### 4.3 Guest→host reentrancy

A hook handler may call `send`, which fires `DispatchCommand` on every plugin — same reentrancy that exists today and is already handled (e.g. `DispatchPluginEvent`'s depth cap, `DrainQuarantine`'s call-site discipline). Host imports must not re-enter *the same instance* while it's executing except where Lua semantics already allow it (an emit from inside a hook dispatches synchronously, including back into the emitter). Wasmtime supports guest reentrancy; the depth cap in `PluginManager` bounds it.

### 4.4 Enforced permissions

For `lang: "wasm"`, the linker binds only imports covered by the manifest's `permissions` declarations (undeclared → bind a stub that traps with a clear "plugin did not declare permission 'send'" message, so authors find out at first use rather than by instantiation failure — friendlier for additive API growth). Document loudly that this makes wasm the first runtime where the plugins-manager permission list is a contract, not a courtesy. Lua/JS keep declaration-only semantics.

### 4.5 Compilation caching

Wasmtime compilation of a large module isn't free at every session start. Enable Wasmtime's built-in on-disk cache (config flag) or precompile to `.cwasm` at install/rescan time keyed by module hash + wasmtime version, stored next to the plugin's data dir. Decide in W1 after measuring cold-compile time on a realistic module; the built-in cache is likely sufficient and is one line.

## 5. The Rust SDK (`scrye-plugin` crate)

Goal: authoring feels like the Lua API, closures included. The crate owns the ABI so plugin authors never see a pointer.

```rust
use scrye_plugin::prelude::*;

#[scrye::init]
fn init() {
    scrye::print("pathfinder loaded");

    scrye::on_gmcp("Room.Info", |json, _pkg| {
        let room: RoomInfo = serde_json::from_str(&json)?;
        scrye::set_state("plugin.pathfinder.room", &room.num.to_string());
        Ok(())
    });

    scrye::add_alias(Alias::new("^go (.+)$").regex(true), |wildcards| {
        for step in plan_route(&wildcards[0])? {
            scrye::send(&step);
        }
        Ok(())
    });

    scrye::add_panel(Panel::new("Pathfinder")
        .widget(Widget::label("Destination").bind("plugin.pathfinder.dest"))
        .widget(Widget::button("Stop").on_click(|| scrye::send("stop"))));
}
```

Internals: registration functions store the closure in a guest-side `HashMap<i32, Box<dyn FnMut(...)>>` keyed by the hook_id the host returned; the exported `scrye_hook` looks up and invokes. `scrye_alloc`/`scrye_free` delegate to the Rust allocator. Typed `serde` structs for `PanelSpec`/widgets/rules mirror `Scrye.PluginContracts` (generate or hand-write; hand-write for v1, the vocabulary is small). Target `wasm32-unknown-unknown` (no WASI needed since the host provides everything); panics are caught by a hook that routes the message through `scrye::log` before trapping.

Ship with a `cargo generate` template (manifest + lib.rs + release profile tuned for size: `opt-level="s"`, `lto=true`, `strip=true` — a typical plugin lands well under 200 KB).

## 6. Packaging & UX

- `.scryeplugin` zip packaging (`PluginPackage.InstallAllIn`) works as-is — the wasm binary is just another file in the folder. `PluginCatalog` discovery needs only the manifest `lang`/`entry` validation extended.
- Plugins manager shows runtime = wasm and the enforced-permissions badge.
- Author docs: new section in `Scrye-Guide.md` — ABI reference, Rust quickstart, limits table, "when to choose Lua vs JS vs wasm."
- Debugging DX: `scrye::log` → the existing per-plugin log file; trap messages include the wasm backtrace Wasmtime provides (symbolicated if the module keeps names — recommend `debug = true` names section in the template's release profile note).

## 7. Phases

**Phase W0 — Spike (small).** `Wasmtime` NuGet into a scratch project; Rust hello-module; round-trip a string host→guest→host with `scrye_alloc`; measure cold compile + cached instantiation; verify epoch interruption traps a busy loop. *Exit: the mechanics are proven and numbers are recorded.*

**Phase W1 — Runtime core (large).** `WasmPluginRuntime` implementing `IPluginRuntime`: init, line/input/gmcp/channel/lifecycle dispatch, timers (host-side `TimerWheel`, same as Lua), rules, state/vars/store, limits + `SafeDispatch` + diagnostics wiring, factory registration. ABI doc written as a versioned spec file (`docs/scrye-wasm-abi.md`) *before* the code that implements it. Tests: a set of tiny fixture modules (checked-in `.wasm` built from checked-in Rust source) driven through the same `FakeHost` harness `PluginRuntimeApiTests` uses. *Exit: fixture plugins pass the runtime API test suite.*

**Phase W2 — Rust SDK (medium).** `scrye-plugin` crate + template; port one real bundled plugin scenario (a self-contained one — `3s-raid` or a subset of `3s-map`'s pathfinding) as the reference example and integration test. *Exit: reference plugin builds from the template and runs in the app.*

**Phase W3 — Full surface + enforcement (medium).** Panels/widget callbacks (`register_action`, cell/hover/submit/choice), `scrye.data`, emit/on, permission-gated linking, compilation cache decision, memory-cap UX. *Exit: parity checklist against the API 1.7 surface (minus explicit non-goals) all green.*

**Phase W4 — Ship experimental (small).** Feature-flag "experimental wasm plugins" in settings; docs section; publish crate + template; solicit one external author. Graduate the flag after a soak cycle.

## 8. Risks

| Risk | Mitigation |
|---|---|
| wasmtime-dotnet release cadence lags upstream wasmtime | Pin and upgrade deliberately; the binding is officially maintained by Bytecode Alliance; ABI is ours, so a runtime swap (wasmer/wazero-style alternatives) stays possible |
| ABI churn burns early authors | Spec is versioned and written first (W1); additive-only growth rule; v1 kept deliberately small |
| Native dependency (wasmtime lib) per platform | Same shape as the KeraLua native-lib work — reuse its Phase 0 checklist and CI matrix |
| JSON envelope overhead on hot path | Measured in W0/W1; reserved raw fast-path for `on_line` if ever needed |
| Binary plugins are opaque (trust/security optics) | Sandbox is the strongest of the three runtimes and permissions are *enforced*; plugins manager says so; encourage source links in manifests |
| Two big native-runtime efforts colliding | Sequencing: KeraLua Phases 0–2 land the factory seam first; Wasm work is additive after that |

## 9. Effort summary and sequencing vs. the KeraLua migration

Rough relative sizes: W0 small, W1 large, W2 medium, W3 medium, W4 small — comparable in total to the KeraLua migration itself, but fully additive (no existing behaviour at risk, no soak/removal tail). Recommended order: KeraLua Phases 0–2 → start W0/W1 in parallel with KeraLua Phases 3–4 → W2+ at leisure. Both plans share the CI native-artifact groundwork.
