# Scrye

A modern MUD client — the clean-room successor to MUSHclient, built in C# / .NET with an [Avalonia](https://avaloniaui.net/) UI.

Scrye is *inspired by* MUSHclient but does **not** aim for binary or plugin compatibility with it. It reimagines the client for today: Unicode-native, GMCP-first, async, and testable, with a redesigned scripting and plugin model.

## Status

Scrye is a working client in daily use on [3Scapes](https://www.3scapes.org/). Connecting, automation, scripting, plugins, HUD panels and profiles are all implemented and exercised in real play — not scaffolding.

What's there today:

- **Connection** — TCP with TLS, telnet negotiation, MCCP compression, automatic reconnect with backoff, and optional auto-login.
- **Protocols** — ANSI (16 / 256 / truecolour), MXP with clickable command links, GMCP, MSP sound, and MIP.
- **Multi-world** — several worlds connected at once as tabs, each with its own scrollback, automation and plugins. Optionally broadcast a command to all of them.
- **Automation** — triggers (plain or regex), aliases, timers, sequences, highlights, and keyboard macros, with an event debugger and a state inspector for working out why a rule did or didn't fire.
- **Profiles** — a four-layer cascade (Global → MUD → Account → Character). Single values take the deepest layer that sets them; collections merge across layers.
- **Scripting** — a Lua console (`/…` in the command line) and a `world.*` API, running on the session loop so ordering stays deterministic.
- **Plugins** — Lua and JavaScript plugins with a manifest, per-character opt-in, live reload, persistent per-world storage, and declarative HUD panels.
- **Output** — a virtualized renderer, find-in-scrollback, tab completion from seen words, command history, capture panes for routed lines (chat, tells), session logging, and recording/replay.
- **Appearance** — themes, a monospace-only font picker with live preview, and a choice between the modern xterm and classic MUSHclient ANSI palettes.
- **Text-to-speech** for incoming output (Windows only at runtime).

## Architecture

A strict engine/UI split, a staged receive pipeline (bytes → telnet → decode → ANSI/MXP → styled line), a single-threaded session loop for deterministic trigger and script ordering, and batched output to the UI thread.

| Project | What it is |
|---|---|
| `Scrye.PluginContracts` | The plugin-facing contract: the `IPluginHost` API surface, declarative `PanelSpec` widgets, the semantic theme-token vocabulary, the manifest schema, and the plugin API version. No NuGet, no engine — a plugin can reference this alone. |
| `Scrye.Core` | The UI-free engine: connection, telnet, MCCP, ANSI/MXP parsing, automation, state store, profiles, plugins host, logging, replay. Depends only on the base framework and `Scrye.PluginContracts` — **no NuGet references**. |
| `Scrye.Scripting` | The Lua host (MoonSharp) and JavaScript host (Jint), the `world.*` facade, and the plugin manager. |
| `Scrye.App` | The Avalonia MVVM UI: world tabs, output view, HUD panels, settings, profile tree, debugger. |
| `Scrye.Cli` | A dependency-free harness; `--selftest` runs canned bytes through the telnet + ANSI pipeline and prints the parsed lines. |
| `Scrye.Companion.Protocol` | The mobile-companion wire contract: message DTOs, batching and JSON config. References `Scrye.Core` only — no NuGet. |
| `Scrye.Companion.Server` | Kestrel + WebSocket host running inside `Scrye.App`, plus the PWA client and Web Push. Kestrel comes from the shared framework — no NuGet. |
| `Scrye.Core.Tests` | xUnit tests over the engine — parser, telnet, automation, highlights, profiles, state store, MIP, plugins, sequences, replay, logging, reconnect. |

`Scrye.Core` staying NuGet-free is deliberate: it keeps the engine portable and makes it cheap to host somewhere other than the desktop app.

`Scrye.PluginContracts` is split out for a related reason: the plugin API should be referenceable
without dragging in the engine. Today nothing needs that — script plugins are text files and
reference no assembly at all — but the boundary is what keeps the API honest. If a type can't
live in an assembly that knows nothing about disk, sessions or Avalonia, it isn't part of the
contract. The namespace stays `Scrye.Core.Plugins`: this is an assembly boundary, not a rename,
so nothing else in the solution had to change.

## Build & run

Requires the **.NET 10 SDK**. First build restores NuGet packages (Avalonia 12, MoonSharp, Jint, xUnit).

```
dotnet build
dotnet test                                          # engine tests
dotnet run --project src/Scrye.Cli -- --selftest     # offline pipeline demo
dotnet run --project src/Scrye.App                   # the GUI
```

To produce a self-contained Windows build zipped for sharing:

```
./publish-win.ps1              # or: ./publish-win.ps1 -Rid win-arm64
```

The recipient unzips and runs `Scrye.App.exe` with nothing installed. Unsigned builds show a SmartScreen warning on first launch.

**A note on platforms.** The engine and the Avalonia UI are cross-platform by construction, and the only Windows-specific piece is text-to-speech (guarded at runtime). But `Scrye.App` currently targets `WinExe` and only Windows builds are produced and tested, so treat macOS and Linux as untested rather than supported.

## Plugins

A plugin is a folder with a `plugin.json` manifest and an entry script:

```json
{
  "id": "3s-viking-status",
  "name": "3S Viking Status",
  "version": "1.0.0",
  "author": "Joakim",
  "description": "Tabbed Viking status HUD panel fed by the BBE viking feed",
  "mudIds": ["*"],
  "entry": "main.lua",
  "lang": "lua",
  "enabled": true,
  "requires": { "scryeApi": ">=1.1 <2.0" },
  "permissions": ["output.read", "state.write", "ui.panels"]
}
```

The **plugin API is versioned independently of the client** (currently 1.1, and versioned as the
`Scrye.PluginContracts` assembly), so a plugin declares what it needs and an incompatible build
refuses it with a clear message instead of failing mysteriously mid-script. `permissions` are declarations shown to the user before they enable a
plugin — informational today, not a sandbox; see the guide for what actually is and isn't bounded.

Plugins are loaded from `plugins/` next to the executable and from `%APPDATA%/Scrye/plugins`, and are enabled per character. Several 3Scapes plugins ship bundled (`3s-build`, `3s-chaossea`, `3s-chat`, `3s-market`, `3s-raid`, `3s-viking-status`).

HUD panels are **declarative** — a plugin describes widgets and binds them to state paths, and the host renders them:

```lua
scrye.addPanel{
  title = "Viking Status", accent = "accent",
  widgets = {
    { type = "gauge", text = "Health", value = "char.vitals.hp", max = "char.vitals.maxhp" },
    { type = "barlist", bind = "vik.refinery" },
    { type = "table",  bind = "vik.cargo", columns = { "Good", "Qty", "Price" }, align = "lrr" },
    { type = "buttonrow", children = { { type = "button", text = "Raid", action = onRaid } } },
  },
}
```

Colours accept a `#RRGGBB` literal or a **semantic token** (`accent`, `warning`, `dim`, `success`, …)
that each host resolves against its own palette, so one spec follows the user's colour scheme on
the desktop and the phone's palette on mobile. `list` and `table` widgets size themselves to their
bound data, so a variable-length collection no longer has to be composed into a pre-padded text blob.

Because a panel is data rather than drawing code, the same spec can be rendered by something other than the desktop UI — which is what the mobile companion below is built on.

Plugins are also **measured**: everything a plugin does on an output line runs on the session loop,
so Scrye reports callbacks slower than 50 ms and unloads a plugin that fails ten times in a row,
rather than letting one bad script quietly degrade a world.

See **[docs/Scrye-Guide.md](docs/Scrye-Guide.md)** for the user guide and the full `scrye.*` plugin API.

## Mobile companion

Scrye plays from a phone. The desktop keeps the MUD connection and does all the work —
telnet, triggers, scripts, state — while a phone acts as a touch-friendly frontend over a
secure WebSocket. It is not a screen share: the desktop streams *structured data* (styled
output lines, state changes, HUD panel specs) and the phone renders its own UI.

- **Installable PWA** — add to the home screen and it runs standalone: ANSI output with
  tappable MXP links, a pinned prompt, command line and directional pad.
- **Your HUD panels, rendered natively** — gauges, barlists, colorgrids and working buttons,
  from the same `PanelSpec` the desktop uses. Every plugin gets a mobile UI for free.
- **Chat view** — driven by your existing capture-pane triggers, with per-pane unread counts.
- **Notifications** — Web Push while the app is closed, wired to the existing trigger
  `Notify` flag. Scrye is its own push application server; there is no service to run.
- **Resume, not reload** — every output line carries a sequence number, so a phone that was
  asleep replays the gap instead of starting over.

Two new projects, both NuGet-free: `Scrye.Companion.Protocol` (the wire contract) and
`Scrye.Companion.Server` (Kestrel + WebSocket, hosted inside `Scrye.App`).

Start it with `.companion` in any connected world. To reach it from outside the machine,
see **[docs/Scrye-Companion-Setup.md](docs/Scrye-Companion-Setup.md)** — it stays bound to
loopback and Tailscale provides the certificate and the route.

## Roadmap

Per-device pairing and revocation, panel `input`/`colorgrid` interactions, and a desktop
settings panel to replace the `.companion` command. A native Avalonia-mobile client remains
optional — the PWA has not yet run out of road. The full build order and the reasoning
behind each decision are in
**[docs/Scrye-Companion-Design.md](docs/Scrye-Companion-Design.md)**.

## Documentation

- **[docs/Scrye-Guide.md](docs/Scrye-Guide.md)** — using Scrye, and writing plugins.
- **[docs/Scrye-Companion-Design.md](docs/Scrye-Companion-Design.md)** — mobile companion architecture and decisions.
- **[docs/Scrye-Companion-Setup.md](docs/Scrye-Companion-Setup.md)** — reaching Scrye from your phone.

## License

[MIT](LICENSE).
