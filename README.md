# Scrye

A modern, cross-platform MUD client — the clean-room successor to MUSHclient, built in C# / .NET with an [Avalonia](https://avaloniaui.net/) UI.

Scrye is *inspired by* MUSHclient but does **not** aim for binary or plugin compatibility with it. It reimagines the client for today: Unicode-native, GMCP-first, cross-platform (Windows/macOS/Linux), async, and testable — with a redesigned scripting and plugin model.

## Status: walking skeleton (Milestone 1)

This is the initial vertical slice. It stands up the architecture end to end so the whole stack talks:

- **`Scrye.Core`** — the UI-free engine. TCP connection, minimal telnet negotiation, an incremental ANSI (SGR) parser producing immutable styled lines, and a single-threaded per-world session loop that raises events. Depends only on the base framework (no NuGet).
- **`Scrye.Scripting`** — the Lua host (MoonSharp) and the `world.*` API facade. *(stub)*
- **`Scrye.App`** — the Avalonia MVVM UI: multi-world tabs, an output view, an input box. *(minimal)*
- **`Scrye.Cli`** — a dependency-free harness; `--selftest` runs canned bytes through the telnet + ANSI pipeline and prints the parsed lines.
- **`Scrye.Core.Tests`** — xUnit tests for the engine (parser, telnet).

## Architecture

See the design docs (in the project knowledge base): the feature inventory (the spec) and the C# + Avalonia architecture sketch. In short: a strict engine/UI split, a staged receive pipeline (bytes → telnet → decode → ANSI/MXP → styled line), a single-threaded session loop for deterministic trigger/script ordering, and batched output to the UI thread.

## Build & run

```
dotnet build
dotnet run --project src/Scrye.Cli -- --selftest     # offline pipeline demo
dotnet run --project src/Scrye.App                   # the GUI
```

Requires the .NET 8 SDK (or newer). First build restores NuGet packages (Avalonia, MoonSharp, xUnit).

## Roadmap

Milestone 1 (this) → OutputControl (virtualized renderer) → automation (triggers/aliases/timers/variables) → scripting host → protocols (GMCP/MCCP/SSL/MXP) → multi-world UI + settings + logging → plugin format. See the architecture doc for the full build order.

## License

TBD.
