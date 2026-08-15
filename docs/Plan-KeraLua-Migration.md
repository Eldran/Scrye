# Plan: MoonSharp → KeraLua (native Lua 5.4)

*Scrye plugin scripting engine migration — planning document, 2026-08-06.*

> **STATUS (2026-08-07): COMPLETE.** All phases executed 0→5 in two days, compressed from the
> planned release-cycle soak because the sole user validated live at each gate: dual-engine
> parity suites green on win-x64, app run on native with plugins working, default flipped,
> then MoonSharp deleted (runtime, package, engine toggle, `lua-ms` pin). `KeraLuaPluginRuntime`
> is THE Lua runtime; `LuaScriptHost` runs on KeraLua too. The %d audit (§4.5) found zero real
> offenders. The instruction-budget hook (§4.6) landed
> 2026-08-08: a lua_sethook count hook raising a Lua error (the one sanctioned exception
> to the no-lua_error rule — see LuaHost.EnableDispatchBudget's boundary note), armed in
> both Lua hosts, catchable-by-pcall limitation documented. Still out of scope: the formal
> MoonSharp-vs-native perf benchmark (§7 item 5; MoonSharp is gone, moot).
> Kept sections below are the rationale record.
>
> **Read everything after this header in the past tense.** It is the plan as written on
> 2026-08-06, kept for *why* the decision was made, not as a description of the tree today.
> Sections 3 and 5 in particular describe files that no longer exist (`LuaPluginRuntime.cs`,
> `LuaJson.cs`), a `lua-ms` language tag that was removed, and an engine toggle that is gone.
> MoonSharp is not a dependency of any project any more; the ~29 remaining mentions across
> `src/` are deliberate parity notes in doc-comments.

**Decisions already made:** target **KeraLua directly** (the raw P/Invoke binding to native Lua 5.4, not the NLua wrapper), and stage the cutover **side-by-side** — the new runtime lands as a parallel `IPluginRuntime` behind a switch, bundled plugins get validated on it, and MoonSharp is deleted only after a release cycle of proven parity.

---

## 1. Why, and why now

MoonSharp 2.0.0 is a pure-managed Lua interpreter implementing roughly Lua 5.2 semantics; its most recent release predates this work by several years. It was the right bootstrap choice (`Scrye.Scripting.csproj` even says so: *"Swap for NLua later if perf demands"*). The costs today:

- **Performance.** MoonSharp is an AST-walking interpreter; real Lua 5.4 is typically 10–50× faster on script-heavy workloads. `PluginManager.ProcessLine` is the hot path — every plugin, every line, on the session loop — and the big bundled plugins (`3s-viking-status`, `3s-market` and `3s-map`, tens of kilobytes of Lua each at the time of writing) all hang hooks and triggers off it.
- **Correctness/longevity.** We cannot rely on upstream fixes, and Scrye is already working around behaviours it does not share with reference Lua: the market plugin (then `3s-market/main.lua`, since merged into the Trade tabs of `3s-viking-status`) carried three documented workarounds — a `"pattern too complex"` abort on patterns real Lua handles fine, a nested-loop codegen quirk needing a normalize step, and an uninitialised-local-aliasing bug needing explicit `nil` inits. Native Lua 5.4 is the reference implementation, so those become removable. *(In the event two were dropped and the pattern split was kept on merit — it reads better.)*
- **New capability.** The Lua C API gives us `lua_sethook` instruction budgets — a real answer to runaway plugin scripts that `PluginDiagnostics` quarantine can only approximate today (it catches *failing* plugins, not *spinning* ones).

**Non-goals:** no change to the `scrye.*` API surface (API 1.7 in `ScryeApi.cs` at the time; 1.11 today), no change to `IPluginRuntime`/`IPluginHost` contracts, no change to plugin manifests beyond an optional engine override, and the Jint JS runtime is untouched.

## 2. Why KeraLua direct (recorded rationale)

NLua adds `LuaTable`/`LuaFunction` object wrappers and an automatic CLR bridge (`luanet`) on top of KeraLua. We skip it because:

1. **Sandboxing is simpler without a CLR bridge.** With NLua the `luanet`/`import` machinery must be actively locked down; with raw KeraLua, nothing from .NET is reachable unless we push it. The sandbox is allowlist-by-construction.
2. **Scrye already marshals by hand.** `LuaPluginRuntime` never used MoonSharp's auto-binding for the plugin API — it builds the `scrye.*` table explicitly, field by field. Porting that style to the Lua stack API is mechanical. (The one exception is `LuaScriptHost`, which uses `UserData.RegisterType<IWorldApi>()` — see §6.5.)
3. **Lifetime control.** NLua's object wrappers keep registry references alive until finalizers run; with raw refs (`luaL_ref`/`luaL_unref`) we control exactly when a plugin's closures die, which matters for the existing panel-rebuild callback-retirement logic (`_panelActions`).

Package: **KeraLua 1.4.9** on NuGet (NLua org), Lua 5.4, ships native binaries for win-x64/x86/arm64, linux-x64/arm64, osx-x64/arm64 under `runtimes/` — covers everything the Avalonia app targets.

## 3. What has to change — inventory

MoonSharp appeared in exactly one project, `Scrye.Scripting` (it appears in none today):

| File | MoonSharp surface used | Fate |
|---|---|---|
| `Plugins/LuaPluginRuntime.cs` | `Script`, `CoreModules.Preset_SoftSandbox`, `DynValue`, `Table`, `CallbackArguments`, `TablePair`, `Script.Call` | Ported to new `KeraLuaPluginRuntime`; original kept until Phase 5 |
| `Plugins/LuaJson.cs` | `DynValue`, `Table` walking for encode/decode | Ported to stack-based encode/decode |
| `LuaScriptHost.cs` | `Preset_HardSandbox`, `UserData.RegisterType<IWorldApi>` | Ported; `world.*` bound as explicit closures instead of userdata reflection |
| `Scrye.Scripting.csproj` | `PackageReference MoonSharp 2.0.0` | Removed in Phase 5; KeraLua added in Phase 0 |

Everything else is already engine-agnostic by design: `IPluginRuntime`, `IPluginHost`, `PluginManager` (which only touches the concrete type in `LoadOne`'s `Lang` ternary), `PanelSpec`, `PluginDiagnostics`, the store, timers (`TimerWheel` is host-side), and pattern matching (`CompiledPattern` is host-side). This is the payoff of the earlier contract-extraction work and is what makes side-by-side cheap.

Tests that exercise the runtime with **real scripts from disk**: `PluginRuntimeApiTests` (builds `LuaPluginRuntime` directly via a `LoadLua` helper), `MapPluginTests` (runs the actual bundled `3s-map/main.lua`), `PluginApiContractTests`. These become the parity suite (§7).

## 4. The hard parts, named up front

### 4.1 Native dependency

This is the biggest character change: Scrye.Scripting stops being pure-managed. Actions:

- Verify `lua54` native lib loads from the KeraLua package on win-x64 (primary platform, `publish-win.ps1`) in both framework-dependent and self-contained publish, and in the test runner.
- If any exotic target lacks a prebuilt binary, `NativeLibrary.SetDllImportResolver` gives us a hook — but the shipped RID set should cover Scrye's targets.
- CI: tests running the native engine must run on each OS we ship, not just one.

### 4.2 Sandbox is now our job

MoonSharp's `Preset_SoftSandbox` did this for us. Native Lua opens with `io`, `os.execute`, `require`, `load`, `dofile`, `debug` — all forbidden. Build a `LuaSandbox` that:

- Opens only: `base`, `table`, `string`, `math`, `utf8`, `coroutine`, plus a **curated `os`** containing only `time`, `date`, `clock`, `difftime` (exactly what soft-sandbox exposed).
- From `base`, removes: `dofile`, `loadfile`, `load`, `loadstring`, `require`, `collectgarbage` (or stub it), `print` (plugins use `scrye.print`; optionally alias `print` → `scrye.print` as a kindness).
- Never opens `io`, `package`, `debug`. (Keep an internal handle to `debug.traceback` for error reporting *before* discarding the table — attach it as the `lua_pcall` message handler so plugin authors get line numbers, which MoonSharp gave them.)
- `string.dump` removed (bytecode escape hatch), and `load`ing binary chunks impossible since `load` itself is gone. Entry scripts are loaded with `luaL_loadbufferx(..., mode: "t")` — text only — as defense in depth.
- One `lua_State` **per plugin**, mirroring today's one `Script` per plugin: full isolation, and `lua_close` on `Dispose()` frees everything a plugin ever allocated.

Write the sandbox test first: a script that attempts each forbidden global and asserts `nil`.

### 4.3 Exceptions vs longjmp

C# exceptions must never unwind through the Lua C API, and `lua_error`'s longjmp must never unwind through C# frames. Rules, enforced by one helper:

- Every C# function pushed into Lua is wrapped: `try { … } catch (Exception ex) { push error string; return lua_error via KeraLua's Lua.Error(); }`. No exception escapes into Lua's unwinder.
- Every call *into* Lua goes through `lua_pcall` (KeraLua `PCall`) with the traceback handler installed. Errors surface as a status code + message we turn into the existing `Safe`/`SafeCall` reporting path (`_host.Print` + `PluginDiagnostics.RecordFailure`) — the quarantine machinery keeps working unchanged.

### 4.4 Callback lifetimes

`DynValue` fields holding Lua functions (`_lineHooks`, `_actions`, `PluginRule.Run`, …) become **registry references** — an `int` from `luaL_ref(LUA_REGISTRYINDEX)`. Introduce a tiny `readonly struct LuaRef` and a `RefList` helper so the runtime's collections look the same as today. `Dispose()` order: unref everything (or simply `lua_close`, which releases the whole state — but keep explicit unref for the panel-rebuild retirement path, which frees callbacks *mid-life*).

KeraLua pushes C# delegates as `LuaFunction` delegates via function pointers — those delegates must be **kept alive in C# fields** for the lifetime of the state or the GC will collect them out from under Lua. The `KeraLuaPluginRuntime` holds them in a list; this is the classic KeraLua footgun and gets a comment block.

### 4.5 Lua 5.2 (MoonSharp) → 5.4 semantic diffs

The bundled plugins were written against MoonSharp. Known diffs to audit for (grep + parity replay in Phase 3):

- **Integer subtype** (5.3+): `math.floor` returns an integer; `tostring(2^31)` differs; `string.format("%d", 1.5)` **errors** in 5.4 (silently truncated before) — and the bundled plugins use `%d`-style formats ~147 times, so this is the single most likely breakage class; the audit is a grep plus checking each argument's provenance for non-integral values (a `/`-division or GMCP float). `1/2` is `0.5` in both. JSON round-trip: our port of `LuaJson.Encode` should use `lua_isinteger` — this actually *simplifies* the current "integral doubles write without decimal point" dance.
- `unpack` → `table.unpack` (5.2 already, but MoonSharp aliases both — audit).
- MoonSharp **nonstandard extensions**: `|x| x*2` lambda syntax, the `dynamic` module, `moonsharp` global, permissive `!=`. Grep all `main.lua` files for these; any hit is a plugin fix.
- Pattern-matching (`string.match` etc.) edge cases — MoonSharp reimplemented Lua patterns; native is authoritative. Low risk, covered by replay testing.
- `os.time`/`os.date` behaviours now come from the C runtime — verify `!` (UTC) formats used by any plugin.

User plugins may hit the same diffs; that is what the side-by-side toggle and the release-cycle soak are for (§8).

### 4.6 New capability: instruction budget (optional, Phase 4)

`lua_sethook(LUA_MASKCOUNT, n)` with a per-dispatch instruction quota; on trip, raise a Lua error ("plugin exceeded execution budget"), which flows into the existing `RecordFailure` → quarantine path. This turns the diagnostics quarantine from "catches crashing plugins" into "catches spinning plugins" — worth doing while we're in here, but explicitly severable.

## 5. Target architecture

```
Scrye.Scripting/
  Lua/                         (new)
    LuaHost.cs                 thin wrapper over KeraLua.Lua: stack helpers,
                               PCallWithTraceback, LuaRef management,
                               delegate keep-alive list
    LuaSandbox.cs              §4.2 environment construction
    KeraLuaPluginRuntime.cs    IPluginRuntime — structural mirror of
                               LuaPluginRuntime (same fields, same methods,
                               DynValue → LuaRef, Table walk → stack walk)
    LuaJsonNative.cs           scrye.json codec against the stack
  Plugins/
    LuaPluginRuntime.cs        unchanged until Phase 5
    PluginRuntimeFactory.cs    (new) replaces the Lang ternary in
                               PluginManager.LoadOne
```

**Runtime selection.** `PluginManager.LoadOne` currently hardcodes `lang == "js" ? Jint : MoonSharp`. Extract an `IPluginRuntimeFactory` (also the seam the Wasm plan plugs into):

- `lang: "lua"` → engine chosen by a global setting `LuaEngine = MoonSharp | Native` (GlobalSettings + settings UI checkbox, default MoonSharp in Phase 4a, Native in Phase 4b).
- Per-plugin escape hatches for the soak period: `lang: "lua-ms"` forces MoonSharp, `lang: "lua-native"` forces KeraLua. Cheap to support (two string comparisons), removed in Phase 5.
- The plugins-manager UI already surfaces per-plugin info; add the active engine to `PluginInfo` so "which engine is this running on" is never a mystery — consistent with the manager's refuse-loudly philosophy.

## 6. Port notes per file

1. **`BuildApi`** — today builds a MoonSharp `Table`; becomes a sequence of `lua_pushcfunction`/`lua_setfield` into a `scrye` table, plus the nested `json` and `store` tables. Every `Fn(a => …)` lambda becomes a `LuaFunction` reading args off the stack with the same defaulting rules (`Arg`, `Num` helpers get stack-based twins: `CheckStringOr("")`, `CheckNumberOr(0)`).
2. **`ToPanelSpec` / `ToWidgetSpec`** — table walking via `lua_getfield`/`lua_next`. Same shapes, same defaults. The action-id registration (`_actions`, `_buildingActions`, hover ids) is engine-independent logic and moves over verbatim with `DynValue` → `LuaRef`.
3. **`LuaJson`** — encode walks the value at a stack index (use `lua_isinteger` for the integer/decimal split; keep the 64-depth cap and the "empty table is `{}`" rule — document that arrays are detected the same way: keys exactly 1..n). Decode builds tables on the stack from `Utf8JsonReader`/`JsonDocument` exactly as today.
4. **`BuildData` / `ToLua`** — same object-graph walk, pushing onto the stack instead of into a `Table`.
5. **`LuaScriptHost`** — drop `UserData.RegisterType<IWorldApi>`; bind each `IWorldApi` member as an explicit closure on a `world` table (the interface is tiny — this is minutes of work and removes the only reflection-based binding in the codebase).
6. **`ProcessLine` / rules / hooks / timers / dispatch methods** — logic unchanged; only the "call this Lua function with these args" leaf changes (push ref, push args, `PCall`).

## 7. Test strategy

1. **Parameterize the existing suites.** `PluginRuntimeApiTests.LoadLua` and the `MapPluginTests` construction sites take a runtime factory; every existing `[Fact]` becomes a `[Theory]` over `{ MoonSharp, KeraLua }`. The suites already load real scripts from disk through a `FakeHost`, so they are precisely the parity oracle we need. Same for `PluginApiContractTests` where it touches runtime behaviour.
2. **Sandbox tests** (new): forbidden-global probes, text-only chunk loading, os-table curation.
3. **Interop edge tests** (new): error in hook → reported via host.Print + diagnostics, not thrown; Lua error message includes script line; C# exception inside a host binding surfaces as a Lua error, not a crash; unref-on-panel-rebuild actually frees (assert via `lua_gc` count or registry probing in debug).
4. **Bundled-plugin replay parity** (Phase 3): load all 8 bundled plugins on both engines, feed identical recorded sessions (the `SessionRecorder`/`SessionReplayer` infrastructure exists in `Scrye.Core.Events`), compare host-visible effects: Sent, Printed, State, Store, Panels. Any divergence is either a 5.4 semantics fix in the plugin (preferred — write it 5.2-compatible so it runs on both during the soak) or a runtime bug.
5. **Perf benchmark** (informal but recorded): `ProcessLine` throughput over a replayed heavy session with viking-status + map + market loaded, both engines. This is the number that justifies the whole exercise — capture before/after in the PR description.

## 8. Phases

**Phase 0 — Groundwork (small).** Add KeraLua 1.4.9; smoke-test native load on win-x64 dev machine, `publish-win.ps1` output, and CI; spike `LuaHost` far enough to run `return 1+1` under pcall. *Exit: native Lua executes in all environments we ship/test in.*

**Phase 1 — Interop + sandbox layer (medium).** `LuaHost`, `LuaSandbox`, `LuaRef`, traceback pcall, delegate keep-alive, sandbox tests, interop edge tests. *Exit: sandbox test suite green; a hook can be registered, called, error-reported, unrefed.*

**Phase 2 — Runtime port (large).** `KeraLuaPluginRuntime` + `LuaJsonNative` + `LuaScriptHost` port + `PluginRuntimeFactory` refactor; parameterize the test suites. *Exit: full existing suite green on both engines.*

**Phase 3 — Bundled plugin validation (medium).** Replay parity for all 8 bundled plugins; fix 5.4 diffs in plugin code (keeping 5.2 compatibility); grep-audit for MoonSharp extensions. *Exit: replay parity clean; plugins run on both engines.*

**Phase 4a — Ship opt-in.** Settings toggle (default MoonSharp), engine shown in plugins manager, release notes ask plugin authors to flip the toggle and report. Optionally land the instruction-budget hook here.
**Phase 4b — Default flip.** Next release: default Native, `lua-ms` escape hatch documented for stragglers. Soak for a full release cycle.

**Phase 5 — Removal (small, satisfying).** Delete `LuaPluginRuntime`, MoonSharp reference, `lua-ms`, and the toggle; sweep the doc-comments that name MoonSharp outside Scrye.Scripting (`PluginAssets`, `TimerWheel`, `IPluginHost`, `PluginManifest`, `PluginPermissions` all mention it in prose); `KeraLuaPluginRuntime` becomes *the* Lua runtime. No `ScryeApi` bump needed — the script surface is unchanged — but add a release-notes line and a `ScryeApi.cs` history comment noting the engine change and Lua version (authors may now use 5.3/5.4 features: integer division `//`, bitwise operators, `goto`, `utf8`).

## 9. Risks

| Risk | Mitigation |
|---|---|
| Native lib fails to load on some user machine (missing VC runtime, AV quarantine) | KeraLua's lua54 is self-contained; Phase 4a soak with MoonSharp fallback one toggle away; loud load-failure message names the DLL |
| User plugin depends on a MoonSharp quirk we didn't anticipate | Side-by-side soak + `lang: "lua-ms"` per-plugin escape hatch until Phase 5 |
| GC collects a callback delegate → native crash | Keep-alive list in `LuaHost`, done once, tested; the only place delegates are created |
| longjmp/exception mismatch corrupts state | All boundaries go through the two helpers in §4.3; code review rule: no naked `lua_call`, no uncaught C# code in a `LuaFunction` |
| Marshalling overhead eats the interpreter win (string copies per line) | Hot path passes one string in, gets bool/string out — trivially cheaper than MoonSharp's DynValue allocation per call; benchmark in §7 item 5 verifies |
| Test runner can't find native lib | Phase 0 exit criterion; KeraLua nupkg handles this via standard runtimes/ resolution |

## 10. Relationship to the Wasm plan

The `IPluginRuntimeFactory` refactor (Phase 2) is the shared seam: the Wasm runtime registers there as `lang: "wasm"`. Sequence the Lua migration's Phases 0–2 first; the Wasm work then plugs into a factory that already exists. See `Plan-Wasm-Plugins.md`.
