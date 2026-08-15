# scrye-wasm-abi — version 1

The binary contract between Scrye and a `lang: "wasm"` plugin. A wasm plugin is a **core
WebAssembly module** (no WASI — the host provides everything) whose manifest names it as the
entry:

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "lang": "wasm",
  "entry": "main.wasm",
  "permissions": ["commands.send", "state.read", "state.write", "storage.private"]
}
```

The ABI is language-neutral: anything that emits a core module can target it (the shipped
SDK is Rust — `sdk/rust/scrye-plugin`; the test fixtures are C). This document is
authoritative; the runtime (`Scrye.Scripting/Wasm/WasmPluginRuntime.cs`) implements it.

**Versioning.** The import module is named `scrye`. Growth is additive: new imports may
appear in any Scrye release, and modules that don't import them are unaffected (wasm links
only what a module declares). A breaking change would bump `scrye_abi_version` to 2; the
host refuses a module whose version it doesn't speak, by name, at load. The *semantic* API
(state paths, widget vocabulary, JSON shapes) is the plugin API (`ScryeApi`, currently 1.11)
and is shared with the Lua/JS runtimes; `requires.scryeApi` gates it as usual.

## Conventions

- **Strings** cross the boundary as `(ptr: i32, len: i32)` pairs — UTF-8 bytes in the
  guest's exported linear memory (which must be exported as `"memory"`).
- **Host → guest strings**: the host calls the guest's `scrye_alloc`, writes the bytes,
  and passes the pair. Ownership passes to the guest; the guest reclaims it however its
  allocator works (the host never touches it again after the call returns).
- **Guest → host string returns**: imports that return a string return a packed `i64`:
  `(ptr << 32) | len`, pointing at guest memory the guest allocated. `0` as the whole i64
  means **nil/absent**; `ptr != 0, len == 0` means **empty string**. The host copies the
  bytes out before the next call into the guest.
- **JSON** is the envelope for anything structured. The host parses leniently (comments
  and trailing commas tolerated, same as `scrye.data`).

## Guest exports (all required)

| Export | Signature | Meaning |
|---|---|---|
| `scrye_abi_version` | `() -> i32` | Must return `1`. Checked before anything else. |
| `scrye_alloc` | `(len: i32) -> i32` | Allocate `len` bytes (8-aligned), return the pointer, `0` on failure. |
| `scrye_free` | `(ptr: i32, len: i32) -> ()` | Reclaim a block the guest returned to the host (the host calls this after copying a returned string/JSON out). A no-op implementation is legal. |
| `scrye_init` | `() -> ()` | The plugin body: register hooks, build panels. Runs once at load, after linking. |
| `scrye_hook` | `(hook_id: i32, ptr: i32, len: i32) -> i64` | Generic dispatch: the payload JSON for a previously registered hook. Returns a packed JSON result, or `0` for none. |

## Host imports (module `"scrye"`)

Every import is gated by a manifest permission (right column). **For wasm plugins,
permissions are enforced**: an import whose permission is not declared is linked to a stub
that **traps** with a message naming the missing permission — at first *use*, not at load,
so additive API growth never breaks instantiation. (This is stronger than Lua/JS, where
permissions are declarations only.)

Registration imports return a **hook id** — a positive `i32` the host will later pass to
`scrye_hook`. Hook ids are opaque and never reused within one instance's lifetime.

| Import | Signature | Permission |
|---|---|---|
| `print` | `(ptr, len)` | *(always available)* |
| `log` | `(ptr, len)` | `log.write` |
| `send` | `(ptr, len)` | `commands.send` |
| `notify` | `(ptr, len)` | `notifications.show` |
| `sound` | `(ptr, len)` | `sound.play` |
| `capture` | `(pane_ptr, pane_len, text_ptr, text_len)` | `capture.write` |
| `get_state` | `(ptr, len) -> i64` | `state.read` |
| `set_state` | `(k_ptr, k_len, v_ptr, v_len)` | `state.write` |
| `watch_state` | `(ptr, len) -> i32` | `state.read` |
| `get_variable` | `(ptr, len) -> i64` | `variables.read` |
| `set_variable` | `(k_ptr, k_len, v_ptr, v_len)` | `variables.write` |
| `store_get` | `(k_ptr, k_len) -> i64` | `storage.private` |
| `store_set` | `(k_ptr, k_len, v_ptr, v_len)` | `storage.private` |
| `store_set_many` | `(json_ptr, json_len)` — a JSON object of k/v strings | `storage.private` |
| `store_delete` | `(k_ptr, k_len)` | `storage.private` |
| `store_keys` | `() -> i64` — a JSON array of strings | `storage.private` |
| `emit` | `(name_ptr, name_len, data_ptr, data_len)` | *(always available)* |
| `get_data` | `() -> i64` — the manifest's `data` files as one JSON object | *(always available)* |
| `add_panel` | `(json_ptr, json_len)` — see **Panels** | `ui.panels` |
| `register_action` | `() -> i32` — hook id for a widget callback | `ui.panels` |
| `on_line` | `() -> i32` | `output.read` (`output.modify` to gag/rewrite — see payload) |
| `on_channel` | `(filter_ptr, filter_len) -> i32` — empty filter = all | `output.read` |
| `on_gmcp` | `(filter_ptr, filter_len) -> i32` — empty filter = all | `output.read` |
| `on_connect` / `on_disconnect` / `on_prompt` / `on_idle` | `() -> i32` | *(always available)* |
| `on_command` | `() -> i32` | `output.read` |
| `on_event` | `(name_ptr, name_len) -> i32` | *(always available)* |
| `after` | `(seconds: f64) -> i32` | `timers.manage` |
| `every` | `(seconds: f64) -> i32` | `timers.manage` |
| `cancel` | `(hook_id: i32)` | `timers.manage` |
| `add_trigger` | `(json_ptr, json_len) -> i32` | `triggers.manage` |
| `add_alias` | `(json_ptr, json_len) -> i32` | `aliases.manage` |

**Rules** (`add_trigger`/`add_alias`) take
`{"pattern": "...", "regex": bool, "ignoreCase": bool, "send": "...", "run": bool}` —
the same vocabulary as the Lua API (`ignoreCase` defaults true, wildcard patterns are
whole-line anchored). With `"run": true` the return value is a hook id that fires with the
wildcards payload; otherwise the return is `0`.

**Timers**: `after`/`every` return a hook id that both receives the firing (via
`scrye_hook`) and names the timer for `cancel`. Resolution is the host tick (250 ms floor,
fractions honoured), same as Lua.

## Hook payloads and results

`scrye_hook(hook_id, payload)` — the payload depends on what the id was registered as.
The guest knows, because it made the registration call. Results are JSON (packed i64),
`0` when the hook has nothing to say.

| Registered as | Payload | Meaningful result |
|---|---|---|
| `on_line` | `{"line": "text"}` | `{"gag": true}` — suppress the line (needs `output.modify`); `{"rewrite": "new text"}` (needs `output.modify`); anything else/`0` — pass through |
| `on_channel` | `{"channel": "Party", "message": "..."}` | — |
| `on_gmcp` | `{"package": "Char.Vitals", "json": "..."}` | — |
| `on_connect` / `on_disconnect` / `on_prompt` / `on_idle` | `{}` | — |
| `on_command` | `{"command": "text"}` | — (observe-only, like Lua) |
| `on_event` | `{"name": "...", "data": "...", "source": "plugin-id"}` | — |
| `watch_state` | `{"path": "...", "value": "..."}` | — |
| `after` / `every` | `{}` | — |
| rule `run` | `{"wildcards": ["...", "..."]}` | — |
| `register_action` (button/click) | `{}` | — |
| `register_action` (colorgrid cell) | `{"col": 3, "row": 4, "ch": "#"}` | — |
| `register_action` (buttonrow choice) | `{"label": "A", "index": 1}` | — |
| `register_action` (input submit) | `{"text": "..."}` | — |

A trap or ABI violation inside `scrye_hook` is reported (world output + diagnostics) and
counted toward quarantine, exactly like a Lua callback error. It never takes the session
down.

## Panels

`add_panel` takes the panel as JSON in the same shape the Lua table API uses:

```json
{
  "title": "My Panel", "width": 30, "background": "#101010",
  "widgets": [
    { "type": "label", "text": "hello", "color": "#ff0000", "dim": true },
    { "type": "gauge", "bind": "character.hp", "max": "100" },
    { "type": "button", "text": "Go", "action": 7 },
    { "type": "colorgrid", "bind": "map.grid", "weave": true,
      "palette": { "#": "#00ff00" }, "onClick": 8, "onHover": 9, "onRightClick": 10 },
    { "type": "buttonrow", "buttons": [ { "text": "A", "action": 10 } ] },
    { "type": "table", "columns": ["Item", "Qty"], "bind": "market.rows" }
  ],
  "tabs": [ { "title": "More", "widgets": [] } ]
}
```

Where Lua embeds a function (`action`/`onClick`/`onHover`/`onRightClick`/`onSubmit`), wasm embeds a
**hook id** from `register_action` (a JSON number). Rebuilding a panel with the same title
replaces it and retires the previous build's action ids, same as Lua — after a rebuild the
old ids are dead (dispatches to them are dropped).

## Execution model and limits

- Everything runs on the session loop thread; `scrye_hook` is never re-entered
  concurrently. Guest→host reentrancy is normal (an import called from inside a hook),
  and `emit` may synchronously dispatch back **into** the emitting guest (self-events),
  which wasm supports; the manager's emit-depth cap bounds cycles.
- **Deadline**: every host→guest call runs under an epoch deadline (default 100 ms). A
  guest that exceeds it traps with an interrupt; the trap is reported and counted toward
  quarantine. This is a real guarantee Lua plugins don't have: a wasm plugin cannot freeze
  the session loop.
- **Memory**: the store caps linear memory at 64 MB. Growing past it fails inside the
  guest (allocation failure), and instantiation fails for modules that demand more
  up front.
- No WASI: no clocks, no randomness, no filesystem, no environment. What the imports
  provide is all there is — the strongest sandbox of Scrye's three runtimes, and the only
  one whose permission list is enforced rather than declared.

## Lifecycle

1. Host compiles the module and checks `scrye_abi_version` (a wrong version refuses by
   name, like an API-range refusal).
2. Host links the permitted imports (others → trapping stubs) and instantiates.
3. `scrye_init()` runs under the deadline; a trap here is a load failure (reported and
   skipped, like a Lua script that throws at load).
4. Session events flow: registrations made during init (or later) receive `scrye_hook`
   calls. `Tick` drives timers host-side.
5. Unload disposes the store; nothing guest-side survives. `scrye.store` is the only
   persistence.
