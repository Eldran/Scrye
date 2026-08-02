# Wiring the companion server into Scrye.App

`Scrye.Companion.Server` is complete and tested in isolation, but nothing starts it yet.
This is the checklist for the `Scrye.App` pass — the half that can only be verified by
building on Windows.

Design references are sections of `docs/Scrye-Companion-Design.md`.

## 1. Project reference

Add to `Scrye.App.csproj`:

```xml
<ProjectReference Include="..\Scrye.Companion.Server\Scrye.Companion.Server.csproj" />
```

The server already references `Scrye.Core` and `Scrye.Companion.Protocol`, and deliberately
**not** `Scrye.App` — everything it needs arrives through `ICompanionSessionSource`. Keep
that arrow pointing one way.

## 2. Implement `ICompanionSessionSource`

One class in `Scrye.App`, holding `MainWindowViewModel`. **Every method must marshal to the
UI thread** — `ScrollbackBuffer` and the view models are UI-thread-owned, and these are
called from Kestrel threads (§4.1):

```csharp
await Dispatcher.UIThread.InvokeAsync(() => /* touch view-model state here */);
```

| Member | Implementation |
|---|---|
| `GetSessions()` | Map `MainWindowViewModel.Worlds` to `SessionStateMessage`. Cheap; consider caching a snapshot updated on world add/remove so it needs no dispatch. |
| `SubmitCommandAsync` | `world.SubmitText(command, origin)` on the UI thread. **Do not** call `MudSession.Submit` directly — that skips aliases, triggers, highlights and logging (§4). |
| `TryReplayAsync` | `Scrollback.CanReplayFrom(afterSequence)` → if false return `null` (the hub then snapshots); else `LinesAfter` into an `OutputBatchBuilder` starting at `afterSequence + 1`. |
| `GetSnapshotAsync` | Tail of `Scrollback`, the `StateStore` snapshot mapped to `StateUpdateMessage`, and the current `PanelSpec`s. |

Returning `null` from `TryReplayAsync` is the *safe* path — a partial replay silently skips
lines the client never saw (§6).

## 3. Publish from the threads that own the data

**Output — in `WorldViewModel.Flush()`,** inside the existing drain. That 33 ms
`DispatcherTimer` tick already is the batch window; do not add a second batcher (§3.1):

```csharp
// after Scrollback.AddRange(_drainBuffer)
if (_companion is { } hub && _drainBuffer.Count > 0)
{
    long firstSeq = Scrollback.NextSequence - _drainBuffer.Count;
    var builder = new OutputBatchBuilder();
    builder.AddRange(_drainBuffer, firstSeq);
    hub.PublishOutput(builder.Build(SessionId));
}
```

`firstSeq` is derived from `NextSequence` *after* the add, so it is correct even when the
same flush triggered a trim.

**State — from `StateStore.Changed`,** on the session loop:

```csharp
_session.State.Changed += change => hub.PublishState(StateUpdateMessage.From(SessionId, change));
```

Prefer `Changed` over `Watch`: it fires for every leaf without a subscription per subtree,
and it already carries the `Removed` flag (§4).

**HUD — on panel build/removal**, via `PublishHudPanel` / `PublishHudPanelRemoved`.

**Sessions — on world add/remove/connect/disconnect**, via `PublishSessionState`.

`CompanionHub` publish methods are safe to call from any thread: each subscriber owns a
bounded channel, and nothing is read back synchronously.

## 4. A stable `SessionId`

`WorldViewModel` has no id today. It needs one that is stable for the life of the world and
distinct per tab (two characters on the same MUD must differ) — e.g. `"{mudId}-{character}"`
falling back to a GUID for quick-connect worlds. The phone uses it to subscribe.

## 5. Lifecycle and UI

- Start on demand from a "Mobile companion" menu item, not at app launch.
- Show the URL and token so they can be copied or turned into a QR (§7.1 pairing, step 4).
- `await server.DisposeAsync()` on app shutdown.

```csharp
var options = CompanionServerOptions.CreateDefault();   // loopback + fresh token
_companionServer = new CompanionServer(options, new AppSessionSource(mainViewModel));
await _companionServer.StartAsync();
```

## 6. What is deliberately NOT here

- **TLS / LAN binding.** Defaults are loopback + plain HTTP. `127.0.0.1` is a browser secure
  context, so a PWA works fully for bring-up. LAN and remote arrive with the Tailscale cert
  in step 6 — not by setting `BindAddress` to `0.0.0.0`.
- **Per-device keys.** One shared token for now; pairing and revocation are step 4.
  `CompanionServerOptions.MayRunScripts` is the single knob, and it is `false` by default.
- **MXP link taps.** If the client gains a "tap a link" action, send the action **raw** —
  never through `SubmitText`. Prefix dispatch applies to text a human entered, never to text
  the MUD supplied (§7.4).
