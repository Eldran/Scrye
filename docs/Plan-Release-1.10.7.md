# Release plan — 1.10.7 (review of 21 Oct 2026)

A review of the app itself — not the plugins — before the next release: how it stands, what
is worth fixing first, and what can wait. Two read-throughs (the Avalonia UI layer and the
engine), with the serious claims re-checked against the code; **✔** marks a finding verified by
reading it, the rest are the reviewers' and plausible. Struck items get dated, not deleted —
this doc is the memory.

## Where it stands

About 40,000 lines in seven projects: `Scrye.Core` (engine), `Scrye.App` (Avalonia),
`Scrye.Scripting` (Lua / JS / wasm runtimes), `Scrye.PluginContracts`, the companion server and
protocol, and `scrye-cli`. CI builds and tests on Windows and Linux on every push (macOS
weekly); a `v*` tag produces a draft release. Since v1.10.6 (6 Oct) the app has two changes —
the reconnect retry budget (5b9fabb) and the numpad operator macro keys (575d861); everything
else was plugin work, already shipped through the catalogue. All sixteen client `.` commands
are in the Guide — nothing has slipped since the August command-surface audit.

**Done well:** one serialised session loop with per-message try/catch and stale-MCCP
generations; bounded buffers everywhere (50k scrollback with chunked trims, telnet SB 1 MB,
CSI/MXP caps, event ring); defence in depth for plugins (Lua instruction budget that survives
`pcall`, `os` rebuilt, `load`/`string.dump` gone; Jint time/memory/recursion limits; wasm epoch
deadline + 64 MB; plugin regex timeouts; quarantine); credential hygiene (OS secret store over
stdin/ArgumentList, password sent as raw bytes and never logged); the companion server binds
127.0.0.1, uses a per-run 256-bit token compared in fixed time, and checks Origin before the
WebSocket upgrade; UI threading is explicit (HUD updates coalesced per widget, hidden
inspectors do no work).

## Before the release — small

All seven done 21 Oct, uncommitted — see *What was done* below for the details and what to
try after the build.

| # | What | Where | Fix |
|---|------|-------|-----|
| R1 | **Done 21 Oct.** ✔ An exception on the UI thread closes the app: nothing hooks `Dispatcher.UIThread.UnhandledException`. Opening Settings with a damaged `global.json` throws — the very thing the startup toast tells you to do. | `CrashLog.cs`; `MainWindowViewModel` OpenSettings / EditNode / SaveGlobal / DeleteNode / QuickConnect; `.log` | A dispatcher catch-all (log + toast, handled; a burst of 20 in 10 s is left to crash) and guards on those actions. |
| R2 | **Done 21 Oct.** ✔ Sidebar **Delete** removes a MUD with every account, character, trigger and alias under it — recursive, permanent, no confirmation. Plugin **Remove** deletes the folder the same way. | `MainWindowViewModel.DeleteNode`; `PluginsViewModel` Remove | Confirm, naming what goes. |
| R3 | **Done 21 Oct.** ✔ Macros fire while typing in other text boxes: the macro handler bubbles, and a TextBox does not consume numpad digits — with numpad walking bound, typing a number into a Settings field or a plugin box sends walk commands. Same for NumPlus/NumMinus. | `MainWindow.axaml.cs` OnWindowKeyDown | Skip macros when focus is in a TextBox other than the command line. |
| R4 | **Done 21 Oct.** ✔ One malformed GMCP message can swallow game output: `TelnetLayer` raises the subnegotiation before resetting `_state`, so a throwing handler leaves it in `SbIac` and the following output is read as GMCP. `RaiseGmcpChannel` calls `GetString()` on non-string fields and catches only `JsonException`. | `TelnetLayer.cs`; `MudSession.RaiseGmcpChannel`; `StateStore.SetJson` | Reset state before raising; type-check the chat fields; catch around state merge. Test: text + bad GMCP + text in one chunk. |
| R5 | **Done 21 Oct.** ✔ A logging failure blocks the display: `RaiseLine` logs before it shows, and the logger does not catch I/O errors — a full disk or a removed log folder stops the line and the rest of the chunk. | `MudSession.RaiseLine`; `SessionLogger` | The logger closes itself on an I/O error and says so once; the session reports it and stops logging. (It still logs before it shows — with the logger no longer throwing, the order stopped mattering.) |
| R6 | **Done 21 Oct.** ✔ TLS accepts any certificate: turning TLS on sets `AcceptInvalidCertificates` too (3Scapes does not use TLS). | `WorldEditorViewModel`; Quick Connect | A separate "accept self-signed" option, off by default. |
| R7 | **Done 21 Oct.** Polish: Escape / Enter in the Settings and editor overlays; rate-limit a plugin that keeps failing (today one that fails every few lines prints an error line forever and never quarantines); a time limit on your own trigger patterns as plugins already have; Linux/macOS wording ("Windows Credential Manager" everywhere, a speech toggle where speech cannot work). | various | — |

## What was done (21 Oct)

Engine, built and tested here — new tests in `ReleaseReviewTests.cs`, each fix reverted in
turn to confirm a test fails (18 mutations, all caught); the Core-only suite is 832/832 in the
container. 13 tests, counting the one added after the review. App and runtimes edited but **not compiled here** (no NuGet) — VS is the first build.

- **R1** `CrashLog.InstallUiThread` (wired in `App.axaml.cs`): a UI-thread exception is logged,
  toasted at most once per 10 s, and marked handled. Guards: Settings opens on defaults when
  `global.json` will not load; Edit says why and does *not* open (defaults saved over a
  character would lose its rules); Save Settings, Delete and Quick Connect toast instead of
  throwing; `.log` says "could not start logging".
- **R2** Delete and plugin Remove take two presses: the first names what goes (the MUD with
  *n* accounts and *m* characters) and relabels the button **Confirm delete** / **Confirm
  remove**; a second within 8 s does it. Selecting another node disarms.
- **R3** Macros are skipped when focus is in any TextBox but the command line, and while an
  overlay is open.
- **R4** `TelnetLayer` resets state before raising, and every event it raises runs under a
  guard that reports through `HandlerFailed` (shown as a `[scrye] error handling a
  telnet/GMCP message` line). Chat fields are type-checked. Covers `StateStore.SetJson` too,
  since it runs inside the subnegotiation handler.
- **R5** `SessionLogger` catches I/O errors, closes itself, raises `WriteFailed` once. The
  session prints `[log] transcript stopped …` and raises `LogFailed`, which clears the app's
  logging flag so `.log` and auto-log start afresh.
- **R6** "Accept self-signed" beside TLS in the profile editor and Quick Connect, shown only
  with TLS ticked, off by default. A profile saved before loads with it ticked (it was implied),
  so an existing TLS world connects as before.
- **R7** Esc = Cancel on the top overlay, Enter = Connect in Quick Connect (not Enter = Done in
  the forms — they would close mid-edit). Plugins print 3 error lines a minute, then one "keeps
  failing", then a count; 30 failures in a minute quarantines regardless of successes. User
  trigger/alias regexes have a 250 ms match limit, skipped with a once-per-rule notice. Password
  hint and the two `%APPDATA%` texts are platform-neutral; the TTS toggle is hidden off Windows.
- Guide updated: TLS / self-signed and two-press Delete (Connecting), Esc/Enter (main window),
  macros in text boxes and the pattern time limit (Automation), a failed log (Logging), the
  new quarantine and error-line rules (When a plugin misbehaves).

**Reviewed** by a second read of the app diff (no compile errors found; Avalonia 12 API names
checked). It found, and these were fixed: Quick Connect did not own the keyboard the way
Settings and the editor do — a click on its padding or an alt-tab handed focus to the hidden
command line, where Enter sent what was typed there to the MUD (an old gap, made worse by
Enter = Connect); it now opens with focus in the host box, and a blank host or bad port keeps it
open with a toast. And an old log failing its last flush as `.log html` replaced it reported
logging as off while the new one ran — the session now swaps before closing and only the
current log reports.

**To try after the VS build** (none of this ran here):

1. Corrupt `global.json` (add a stray `{`), start: the toast; press ⚙ Settings — opens on
   defaults with a second toast, no crash.
2. Sidebar Delete on a test character: toast + "Confirm delete"; wait 8 s — back to Delete;
   press twice — gone. Same for plugin Remove on a throwaway plugin.
3. Bind NumPlus to something, open a plugin panel with an input box (or the find bar), type
   `+` and digits — they type, nothing is sent. The command line still fires the macro.
4. Settings / editor / Quick Connect: Esc closes; Enter in Quick Connect's host box connects.
5. Quick Connect: opens with the cursor in the host box; Enter on an empty host keeps it open;
   click its padding, then Enter — connects, nothing is sent to the world behind. With TLS
   ticked the self-signed box appears under it.
6. A trigger with pattern `(a+)+$` (regex) and `say aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!` — a
   `[trigger] … took over 250 ms` line once, and the game keeps flowing.
7. Linux, if handy: no TTS toggle; the password hint names the keyring.

Known and left: Escape discards an edited form without asking (as Cancel does); Save on the
defaults Settings form replaces a damaged `global.json` (as the startup toast already says);
deleting a profile still leaves its saved password in the OS store. Accept self-signed follows
the cascade like TLS does — a layer can switch it on but not back off, so an inherited "on" is
turned off where it was set. A Quick Connect or profile connect that fails after its tab is added
leaves the tab (the same as before).

## Second round (21 Oct) — what was done

Engine work built and tested in the container, as before. New this round: **the Lua half of
Scrye.Scripting was built and run here too** — Lua 5.4.8 compiled from KeraLua 1.4.9's own
source, KeraLua's C# compiled in, Jint/wasm left out — so the Lua runtime and the existing
real-Lua tests (`LuaSandboxTests`, `FarmerPluginTests`, `ChaosSeaDelegationTests`) ran against
the real thing. Core-only suite 855/855, real-Lua 27/27, 18 plugin harnesses all green.
Mutation passes: reconnect 6/6 (plus the generation one three times over), idle flush 8/8,
memory limit 8/8 — the last run under a 3 GB address-space cap so a mutant that lets a
plugin grow forever could not take the machine with it.

- **Reconnect.** `Connection` tags each connect with a generation: a read loop belongs to the
  connect that started it, and when superseded it closes its own socket and says nothing.
  Before, its late `finally` nulled the shared stream — by then the new connection's — and
  reported Disconnected after the new Connected, so connecting over a live link killed it.
  (Dormant until now: nothing reconnected while a loop was alive.) A connect attempt can be
  cancelled on its own (`attemptCt`) without tying the new socket's lifetime to it.
  `MudSession.ReconnectAsync` stops any retry series (cancelling an attempt still dialling),
  waits for it, resets the budget and connects through a gate that lets one connect run at a
  time; a retry that finds the link already up stands down; a late Failed/Disconnected never
  starts a series over a live connection. App: the button (`CanReconnect`), `.reconnect`,
  the double-click, and "gave up" now names both. A second read of the app diff caught two
  things, fixed: the double-click reconnected with the settings the tab was MADE with (a host
  or password fixed since was ignored, where before a fresh tab picked it up) - it now passes
  the profile as it is now (`ReconnectAsync(settings)`); and the button could show on a
  brand-new tab before its first state arrived, where a press replaced the connection being
  made - "down" now means a connect that has reported Disconnected/Failed, and a reconnect or
  a connect in flight is never replaced. Also: a failed reconnect says why, the button does
  not take keyboard focus, `.reconnect` with no host says so.
- **Idle flush.** After 300 ms of quiet, an unfinished line that *looks like a prompt*
  (ends `> : ? ] ) #`) is emitted as a prompt; the newline the server then sends is swallowed
  so the lines are exactly today's, only earlier. Half a sentence is never cut. Off for the
  connection once a GA/EOR arrives. Engine property `IdleFlushEnabled` (no UI switch).
- **Lua memory cap.** `LuaHost.EnableMemoryLimit` wraps the state's own allocator and counts.
  The hard part is *where* it may refuse: a refused allocation raises an error at that spot,
  and outside a pcall that is a panic (process abort), inside a C# binding a longjmp across
  .NET. So it refuses only inside the native `lua_pcall` and never while a binding or the
  traceback runs; Lua called back from a binding is limited again under its own pcall.
  Three things only running the real Lua found: KeraLua spells it `LuaGC.Countb` (the VS build
  would have failed); a host dropped without `Dispose` is closed by KeraLua's finalizer through
  the allocator — the delegate must be rooted (GCHandle) or the process dies; and
  `luaL_Buffer` (string.rep, table.concat) asks the allocator directly, with no emergency
  collection, so after a plugin ran out its garbage kept refusing the next strings — the host
  now collects after an outermost call that ran out. Cost: a managed hop per allocation,
  ~1.7× on an allocation-only benchmark, microseconds per line in practice.
- **Plugin harnesses in CI.** `tests/plugins/*_test.lua` (paths one level deeper, header notes
  updated), `tests/plugins/run.sh`, and a `Plugin harnesses` job (Ubuntu, apt lua5.4) the CI
  gate now also requires. One harness had gone stale unnoticed: `raidgmcp_test.lua` failed 3
  checks against 3s-raid-gmcp 2.3.2 — all deliberate plugin changes per its changelog (the
  baked-in town lists, the Raids tab and its `table` widget); the three checks were rewritten
  to pin the current behaviour (the foreign-pool one reads FOREIGN_DEFAULT from the plugin)
  and mutation-checked. The harnesses carry verbatim game text (character names, other
  players' in combat lines); they are in the repo now.

**To try after the VS build:**
1. Drop a world (kill the connection or wait for a reboot) — ⟳ Reconnect appears at the left
   of the bottom bar; press it, scrollback stays. `.reconnect` on a live world replaces the
   link cleanly. Double-click an open character: goes to its tab.
2. Log in on 3Scapes and watch prompts and triggers — nothing should look different: the
   prompt-gated sequences working on 3Scapes means its `>` already arrives as a line of its
   own. If a line ever splits that used to be one, that is this, and it is one switch off. On a MUD whose login prompt has no newline, auto-login now
   answers without waiting.
3. The plugins load and behave as before (the memory cap is invisible below 256 MB).
4. After pushing: the Actions run shows a **Plugin harnesses** job, green.

## Worth considering — a bit bigger

The first four were done 21 Oct, at Joakim's request, before the release — see *Second
round* below.

- ~~**Reconnect**~~ — **Done 21 Oct.** A ⟳ Reconnect button while a world is down, `.reconnect`,
  and a double-click on an open character goes to its tab (reconnecting it if it dropped).
- ~~**Idle flush of an unfinished line**~~ — **Done 21 Oct.**
- ~~**Lua memory cap**~~ — **Done 21 Oct**, 256 MB like JS.
- ~~**Plugin harnesses in CI**~~ — **Done 21 Oct**: the 18 harnesses of shipped plugins moved
  from `_lab` to `tests/plugins` (lab plugins' harnesses stay), and CI runs them.
- MCCP end-of-stream (a copyover or `compress off` can lose the first output after it); a cap
  on an unterminated line; coalescing queued ticks after a stall.

## After the release — refactors

- **One rule editor**: the Trigger / Alias / Timer / Sequence tabs exist twice in
  `MainWindow.axaml` (world editor and Global Settings, ~800 identical lines). One
  UserControl over a shared view-model base — and macros and variables gain a per-layer tab,
  which they lack today although `.import` writes them per character.
- **WorldViewModel** (2300 lines): client commands → a `ClientCommands` class; capture panes
  → a pane host; the 385-line constructor into wiring helpers. HudViewModel's widget classes
  → their own file (no risk, any time).
- **Connection teardown** as one idempotent, generation-tagged method — safe with today's
  callers (verified), but a Reconnect button makes it matter, so do it with that.
- **OutputView** re-shapes every visible run each frame; cache per line. Profile first.
