# Scrye Mobile Companion — Architecture & Design

**Status:** Design / proposal · **Last updated:** 2026-08-02

*Rev. 8 — steps 5 and 7 done (resume/snapshot was already built; Web Push shipped).
Chat view, HUD panel rendering and `permessage-deflate` landed. §3 documents `output.pane`
and `hud.action`.*
*Rev. 7 — step 3 done: installable PWA on the home screen, and tailnet identity replaces
token typing (new §7.5). `hud.action` and `output.pane` added to the protocol.*
*Rev. 6 — step 6 done: reachable from a phone over a real certificate, 2026-08-02.
Setup walkthrough in `Scrye-Companion-Setup.md`.*
*Rev. 5 — step 2 built and wired; protocol proven end-to-end from a browser on 2026-08-02.*
*Rev. 4 — steps 0 and 1 built. §7.3 narrowed to gate only `/` (not `.` sequences); new
§7.4 retracts a recommendation that would have made MXP links executable; §10 marks
progress.*
*Rev. 3 — protocol and seams reconciled against the actual engine: §3 rewritten (batched
frames, style table, corrected state DTO), §4 verified with a new §4.1 on threading, §6
scrollback reuse, new §7.3 on the scripting-permission gap, §11.4 closed.*
*Rev. 2 — §11 decisions resolved; §5, §7.1, §7.2, §8, §10 revised to follow.*

This document describes how Scrye becomes a "full-circle" MUD platform: the desktop
client keeps doing the hard work (Telnet, ANSI, triggers, scripts, maps, state) and a
mobile — or browser — companion acts as a thin, touch-friendly frontend over a secure
connection to the PC.

It is a decision record, not a task list. It captures *why* the architecture is shaped
the way it is, what already exists in the codebase to build on, and the order in which
the pieces should be built.

---

## 1. The core idea

```
MUD server
    ↕ Telnet / MCCP / GMCP / MIP
Desktop client (Scrye.App on the PC)  ← single source of truth
    ↕ secure WebSocket (LAN / VPN / relay)
Companion frontend (browser first, native later)
```

The desktop remains responsible for everything stateful and long-lived: the Telnet
connection, ANSI/MXP parsing, triggers and aliases, scripts and plugins, timers,
logging, variables, maps, character state, and reconnection. The companion receives
*already-processed* information (output lines, character state, room, map data, chat,
available commands, connection state) and sends back *actions* (send command, run alias,
press a shortcut, switch session, start a route, pause a script).

### Why not remote desktop?

Google Remote Desktop / VNC streams the desktop's pixels to the phone: tiny text,
desktop-sized buttons, awkward zoom, a virtual mouse. A dedicated companion sends
*structured data* instead of a video stream, so the phone renders a proper mobile UI
with its own fonts, layout, and touch controls — while the PC still does all the work.

### Why not a full mobile MUD client?

Because a phone **cannot reliably hold a Telnet socket alive in the background** — iOS
and Android suspend or kill backgrounded network apps. The PC can. Making the desktop
the always-on host and the phone a resumable frontend works *with* the mobile platforms
instead of against them. This single fact drives most of the rest of the design
(sequence-numbered resume, snapshot-on-reconnect, "PC stays connected while the phone
sleeps").

---

## 2. What already exists (build on this, don't rebuild it)

Scrye is further along toward this than a greenfield plan assumes. Three existing
subsystems do most of the work the companion needs:

**An internal event stream is effectively already there.** `MudSession` emits events,
`WorldViewModel` already fans output and state into the HUD, and there is a shared state
tree — `character.*`, `enemy.*`, `vik.*`, `plugin.<id>.*` — that plugins push into with
`scrye.setState` and observe with `scrye.watch`. A `state.update` companion message is
close to a direct serialization of one node in that tree. The companion server is mostly
a *tap* on flows that already exist, not new plumbing.

**ANSI output is already modelled as styled spans.** `OutputView` renders styled runs
(foreground/background/bold) today. An `output.line` message with a `spans` array maps
onto the existing line model — there is no need to parse ANSI a second time for the wire.

**The HUD panel spec is already a serializable UI description.** Plugins build panels
declaratively:

```lua
scrye.addPanel{
  title = "Viking Status", accent = "#46B45A",
  widgets = { {type="gauge", ...}, {type="barlist", ...}, {type="buttonrow", ...} }
}
```

Buttons, gauges, `barlist`, `colorgrid`, `input`, `value`, `label`, `progress`, `text`
— these are *data*, not pixels. If the companion streams the `PanelSpec` and the phone
renders it natively, then custom command buttons, status gauges, refinery bars, travel
lists, and auto-trader controls all cross over for free, and **every plugin written from
now on automatically gains a mobile UI**.

This is the most important architectural consequence in the whole document: the companion
is not a second interface to design and maintain in parallel — it is a **second renderer
of specs the desktop already emits.** The protocol should be built around that from day one.

---

## 3. Message model

Structured messages, never screenshots. JSON over WebSocket for the live channel; a small
request/response surface (over the same socket or plain HTTPS) for setup and history.

*The shapes below were reconciled against the engine on 2026-08-02; §3.3 records what the
first draft got wrong and why.*

Output frame — a **batch** of lines plus the style table they index into (§3.1):

```json
{
  "type": "output.batch",
  "sessionId": "threescapes-eldran",
  "styles": [
    { "fg": "#AA0000", "bg": "#000000" },
    { "fg": "#FFFFFF", "bg": "#000000", "flags": ["bold"] }
  ],
  "lines": [
    {
      "sequence": 18422,
      "timestamp": "2026-08-01T17:58:31+02:00",
      "prompt": false,
      "spans": [
        { "text": "The wiremouth ", "s": 0 },
        { "text": "attacks you!",  "s": 1 }
      ]
    }
  ]
}
```

A span may also carry `"link": { "action": "kill wiremouth", "isUrl": false, "prompt": false }`
— see §4 on MXP links.

State update. `kind` mirrors `StateKind`; `text` is the canonical form, so the phone can
render a string leaf without guessing:

```json
{ "type": "state.update", "sessionId": "threescapes-eldran",
  "path": "char.vitals.hp", "kind": "number", "text": "812" }
```

There is deliberately no `maximum` field — see §3.3.

Command from the phone:

```json
{ "type": "command.send", "sessionId": "threescapes-eldran", "command": "north" }
```

HUD panel spec (streamed, then updated in place):

```json
{ "type": "hud.panel", "sessionId": "...", "panelId": "viking-status",
  "spec": { "title": "Viking Status", "accent": "#46B45A", "widgets": [ ... ] } }
{ "type": "hud.state", "sessionId": "...", "panelId": "viking-status",
  "bind": "vik.refinery", "value": [ ... ] }
```

Capture-pane output — a **separate stream** from the main batch, because a trigger can
capture *and* gag, so a routed chat line often never reaches the main output at all (§3.4):

```json
{ "type": "output.pane", "sessionId": "...", "pane": "Chats",
  "styles": [ ... ], "lines": [ ... ] }
```

Panel button, client → desktop. Carries only an id the desktop itself published, so it
grants a device nothing beyond firing its own plugins' callbacks:

```json
{ "type": "hud.action", "sessionId": "...", "panelId": "3s-raid|Auto Raid", "action": "btn-arm" }
```

Push registration, client → desktop (§7.2):

```json
{ "type": "push.subscribe", "endpoint": "https://web.push.apple.com/...",
  "p256dh": "...", "auth": "..." }
```

Session state:

```json
{ "type": "session.state", "sessionId": "...", "connected": true,
  "character": "Eldran", "world": "ThreeScapes", "room": "Megacity" }
```

### 3.1 Throughput matters

3Scapes combat can emit hundreds of lines per second. Do **not** send one WebSocket frame
per line with a full span array each. Three mitigations, in order of value:

- **Batch/coalesce** output lines into a single frame. Scrye already does exactly this:
  `WorldViewModel` queues incoming lines on a `ConcurrentQueue<Line>` and drains them into
  `ScrollbackBuffer` from a `DispatcherTimer` at **33 ms**. That existing flush *is* the
  batch window, and it is tighter than the 50–100 ms this document originally asked for.
  Tap the drain; do not build a second batcher.
- **Per-frame style table.** Within one 33 ms batch only a handful of distinct
  (fore, back, flags) combinations occur. Emit them once as `styles[]` and have each span
  carry an index `"s"`. This is where the wire savings actually are.
- **`permessage-deflate`** on the WebSocket. *Enabled 2026-08-02* via
  `WebSocketAcceptContext.DangerousEnableCompression`, together with
  `DisableServerContextTakeover`. The "Dangerous" name is about CRIME-style size oracles
  when a stream mixes a secret with attacker-chosen text; no credential travels inside this
  socket (the token rides on the handshake URL), and resetting the compression context per
  message removes the cross-message oracle regardless. The cost is small here because a
  frame is already a whole batch rather than one short message.

Note what is *not* on that list: the original "palette-index spans" idea. It cannot be
built — see §3.3.

### 3.2 C# contract (`Scrye.Companion.Protocol`)

```csharp
// One frame carries many lines and the style table they index into.
public sealed record OutputBatchMessage(
    string SessionId,
    IReadOnlyList<StyleDto> Styles,
    IReadOnlyList<OutputLineDto> Lines);

public sealed record StyleDto(string Fg, string Bg, RunFlags Flags);

public sealed record OutputLineDto(
    long Sequence, DateTimeOffset Timestamp, bool IsPrompt,
    IReadOnlyList<OutputSpanDto> Spans);

// StyleIndex points into OutputBatchMessage.Styles. Link is non-null for MXP
// <SEND>/<A> runs and auto-detected URLs (Line.Links already computes these).
public sealed record OutputSpanDto(string Text, int StyleIndex, LinkDto? Link = null);

public sealed record LinkDto(string Action, bool IsUrl, bool Prompt, string? Hint);

// Mirrors Scrye.Core.State.StateValue: a kind plus the canonical text form.
public sealed record StateUpdateMessage(
    string SessionId, string Path, StateKind Kind, string Text, bool Removed);

// Source is what lets the server apply per-device permissions (§7.3).
public sealed record SendCommandMessage(string SessionId, string Command);

public sealed record SessionStateMessage(
    string SessionId, bool IsConnected, string? CharacterName, string? WorldName);

public sealed record HudPanelMessage(string SessionId, string PanelId, PanelSpecDto Spec);
```

`RunFlags` and `StateKind` are reused from `Scrye.Core` rather than redeclared, so
`Scrye.Companion.Protocol` references `Scrye.Core` — but **not** `Scrye.App`, which is
`WinExe` and pulls in the Windows-only `System.Speech`. Keeping that boundary clean is
what lets a future Avalonia-mobile head (§8.2) share the contract.

These DTOs are the one contract both sides code against, and the only genuinely new
shared code the project needs.

### 3.3 What the first draft got wrong

Three corrections, all found by reading the engine rather than reasoning about it. They
are recorded rather than silently fixed because each one rules out a tempting redesign.

**Palette-index spans are impossible.** `StyledRun` carries `Rgb Fore, Rgb Back` —
24-bit colour. `AnsiParser` resolves 16- and 256-colour codes through `Rgb.Ansi16` /
`Rgb.Xterm256` at parse time and **discards the index**; `Rgb.AnsiPalette` is a static
whose changes only affect lines parsed afterwards. There is no index left to put on the
wire, and the desktop itself cannot re-theme existing scrollback. Send resolved hex,
compressed via the style table.

**`state.update` cannot type its value as a number.** `StateValue` is
`Null | String | Number | Bool` with a canonical `Text` form. `char.name` and
`room.exits.0` are strings; typing the wire field as `double` silently destroys them.
Mirror the kind + text. Also carry `Removed`, because `StateStore` genuinely removes
leaves (`ClearPrefix`, and the diffing resend inside `SetJson`) and the phone must be able
to drop them rather than keep a stale value.

**There is no `maximum`.** Max is a *sibling path* — `char.vitals.hp` and
`char.vitals.maxhp` are two independent leaves. `WidgetSpec` already models this correctly
with separate `Value` and `Max` path strings, and the companion should follow it. The
original `double? Maximum` invented a pairing the state tree does not have.

**Not wrong, just missed:** `Line` also carries `IsPrompt` (a line flushed by telnet
GA/EOR — the server is waiting for input) and `Links`, and `RunFlags` has Underline,
Italic, Blink and Inverse alongside Bold. All are now in the contract. `IsPrompt` matters
more on a phone than on the desktop: it marks where the input bar should anchor instead of
letting the prompt scroll away.

### 3.4 Why capture panes are their own stream

The obvious design is a `pane` field on `OutputLineDto`. It does not work.

Capture-to-pane and gag are **independent trigger actions**, and the usual chat setup uses
both: route the line to a pane *and* hide it from the main output. A gagged line never
reaches `LineReady`, so it never enters `ScrollbackBuffer` and never appears in an
`output.batch`. Tagging main-stream lines would therefore miss precisely the lines a chat
view exists to show.

`output.pane` carries real sequence numbers, because each `CapturePaneViewModel` owns its
own `ScrollbackBuffer` — so per-pane resume is possible later without redesign. It is not
implemented yet; instead `session.snapshot` carries recent pane history, so a reconnecting
client rebuilds rather than starting empty. That matters: a reconnect is exactly when you
want to catch up on what was said while the phone was asleep.

---

## 4. Integration seams in the current codebase

The companion touches Scrye at a small number of well-defined points. Type and member
names below were verified against the source on 2026-08-02.

- **Output out:** tap `WorldViewModel.Flush()` — the `DispatcherTimer` drain that moves
  `_pending` into `Scrollback` (§3.1). The `_drainBuffer` it builds is exactly one
  companion frame. Assign each line a monotonic `sequence` here, as it is appended.
- **State out:** `StateStore.Changed` (an `Action<StateChange>` carrying path, value and a
  `Removed` flag) is a better tap than `Watch`, because it fires for every leaf without
  needing a subscription per subtree. This is the same channel `scrye.watch` sits on.
- **HUD out:** serialize `PanelSpec` on panel build and stream `setState` bindings as
  `hud.state` deltas. `PanelSpec`/`WidgetSpec` live in `Scrye.Core.Plugins` and are pure
  records with no Avalonia dependency — including `Tabs` and `buttonrow` children — so
  this needs no adapter layer at all. §2's central claim holds up in full.
- **MXP links (free win):** `Line.Links` already computes clickable spans from MXP
  `<SEND>`/`<A>` runs and auto-detected URLs, and `WorldViewModel.HandleCommandLink(
  command, prompt)` already routes a click. Carrying `LinkDto` on the wire makes MUD text
  tappable on the phone for almost no work — a better affordance than the fixed command
  pads in §8.3, because the MUD authors it.
- **Commands in:** the intent is right — phone input must run through aliases, triggers,
  highlights and logging exactly as typed input does — but `WorldViewModel.Submit()` is
  **private and reads the `Input` property**, so the companion cannot call it without
  poking `Input` and racing the UI thread. Extract:

  ```csharp
  public CommandSubmitResult SubmitText(string text, CommandOrigin origin);  // Submit() passes CommandOrigin.Local
  ```

  **Built (rev. 4).** `CommandOrigin` is a readonly record struct carrying the source plus
  `MayRunScripts`; `Submit()` now only reads the input box, records history/completion and
  delegates. Note that history and completion stay in `Submit()`: they belong to *that*
  input box, and a companion device keeps its own.

  and route `command.send` through it with `source: Companion`. That parameter is also the
  hook the permission check in §7.3 needs — which is not optional, see there.
- **Sessions:** the companion enumerates `MainWindowViewModel.Worlds` (an
  `ObservableCollection<WorldViewModel>`); switching the active session on the phone just
  changes which `sessionId` it subscribes to. The desktop keeps every world connected
  regardless. Note `MainWindowViewModel.Broadcast` / `WorldViewModel.ReceiveBroadcast`
  already exist for send-to-all-worlds; expose that as an explicit companion action rather
  than letting the phone toggle `IsBroadcast` behind its own back.

### 4.1 Threading — get this right on day one

Three different threading contracts meet in the companion server, and none of them is a
Kestrel request thread:

- `StateStore` is **single-threaded by contract** — "fed and read on the session's mailbox
  loop; UI/plugin consumers marshal to their own thread."
- `ScrollbackBuffer` and `Flush()` are **UI-thread**, driven by the `DispatcherTimer`.
- WebSocket sends happen on **Kestrel threads**.

So the server must never read `StateStore` or `Scrollback` directly from a socket handler.
Give it its own outbound queue, filled from the flush timer and the state-change callback,
and drained by the socket writers. Done this way it works under load; done the obvious way
it works fine until the first heavy combat round and then corrupts or tears.

---

## 5. Transport tiers

Three ways to connect the phone to the PC. The mesh-VPN tier is listed second for
conceptual ordering only — in practice it is the **primary** target, for reasons that turn
out to be about TLS rather than about remote access (see §7.1).

**Local network (bare LAN).** Phone → `192.168.x.x:port` → desktop, over Wi-Fi. Fast, no
external server, no account. Works while both devices share a network; may need a firewall
allowance. Fine for early protocol bring-up and for a native client that does its own cert
pinning. Its real limitation is *not* reach — it is that a LAN IP with a self-signed cert
is not a browser **secure context**, so the PWA path (service worker, offline shell, Web
Push) cannot work over it. See §7.1.

*Confirmed in practice on 2026-08-02 — see `docs/Scrye-Companion-Setup.md`.*

**User-run mesh VPN (the actual primary tier).** A private WireGuard/Tailscale mesh makes
the phone and PC behave as if on the same LAN, from anywhere, with no public exposure of
the desktop and no service for us to operate. From Scrye's perspective it is still just a
private IP.

Beyond remote access, Tailscale specifically solves the certificate problem: `tailscale
cert` / `tailscale serve` issues a genuine Let's Encrypt certificate for
`machine.tailnet-name.ts.net`, trusted by every browser, while the host stays reachable
only inside the tailnet. That single fact makes it the recommended path **even when both
devices are on the same Wi-Fi**, because it is what lets the browser companion be a real
installable PWA. Treat "install Tailscale" as part of the normal setup story, not as an
advanced remote-access option.

**Hosted relay (deliberately deferred; on current analysis, never built by us).** Both
ends dial out to a relay that bridges them. It "just works" from anywhere — but it is not
a feature, it is a *service*: user accounts, uptime, security liability, bandwidth, and
(for EU users) GDPR responsibility over other people's private MUD chat. Push-while-closed
used to be the one argument that could justify it; §7.2 now explains why it no longer does.

---

## 6. Reconnection & resume

Every output event carries a monotonic `sequence`. The phone remembers the last sequence
it received. On reconnect:

```json
{ "type": "session.resume", "sessionId": "threescapes-eldran",
  "lastReceivedSequence": 18422 }
```

The desktop replays everything after that point from its ring buffer. If the gap is
larger than the buffer, it sends a **snapshot** instead — latest N lines, current
variables, current room, character state, active timers, command shortcuts, and current
HUD panels — and the phone rebuilds from scratch. Because the PC never dropped the MUD
connection, "the phone was asleep for an hour" is just a large resume gap, not a lost
session.

**Use the existing scrollback; do not add a parallel buffer.** `ScrollbackBuffer` already
holds **50,000 lines** by default and trims in 2,000-line chunks. At 3Scapes' line rates
that is a very long resume window, and a second buffer would double the memory for no gain.

**One trap:** `ScrollbackBuffer` is index-addressed and trims from the *front*, so after
the first trim `index != sequence`. Keep a monotonic counter alongside it plus the sequence
of `_lines[0]`; then `index = sequence - baseSequence`, and "is this gap replayable?"
is simply `lastReceivedSequence >= baseSequence`. Getting this wrong produces a resume that
silently serves the wrong lines rather than failing — which is much worse than a snapshot.

---

## 7. Security

The companion can send commands into a live session, read private tells, and see login
state. It is a real attack surface and is designed as one.

Baseline for every build: encryption on the wire, device pairing, authentication on every
connection, revocable per-device keys, rate limiting, per-session permissions, **no
unauthenticated listener**, and a visible log of connected devices. The mobile app should
generally **never receive the saved MUD password** — the desktop handles login and only
ever exposes the *active* session.

Per-device permissions, e.g.:

```
Joakim's iPhone may:
  ✓ View output   ✓ Send commands   ✓ Run aliases
  ✓ Switch sessions   ✓ View maps
  ✗ Edit scripts   ✗ Install plugins   ✗ Change saved passwords
```

That last line is not free — see §7.3.

### 7.1 Pairing

1. Open "Mobile companion" in the desktop client.
2. Desktop shows a QR code containing: host address, a short-lived pairing token,
   the desktop's **public key / TLS cert fingerprint**, and the protocol version.
3. Scan with the mobile app.
4. Approve the device on the PC.
5. Devices exchange keys; the phone **pins** the desktop cert from the QR.

Bind the listener to the LAN/loopback interface; never `0.0.0.0` by default. Prefer
device-specific keys with revocation over a single shared password. Use TLS even on the
LAN.

**Certificates: two paths, and they are not interchangeable.** An earlier draft of this
document claimed a self-signed cert was universally fine because the QR pins it. That
holds for a *native* client and not for a browser, and the browser is the MVP — so the
two cases have to be stated separately:

- **Native client (Avalonia-mobile, §8.2): self-signed + QR pinning.** The client controls
  its own TLS validation, so pinning the fingerprint from the QR is genuinely sufficient.
  No CA involved, no plaintext on the LAN.
- **Browser / PWA client: a real certificate is required.** Browsers do not honour our
  pinning; a self-signed cert produces an interstitial, and — decisively — a
  **service worker requires a genuine secure context**, so without a trusted cert there is
  no PWA install, no offline shell, and no Web Push (§7.2). `http://localhost` counts as a
  secure context; a LAN IP does not.

The clean answer for the browser path is the Tailscale-issued Let's Encrypt cert described
in §5: a real, browser-trusted certificate on `machine.tailnet-name.ts.net`, with no
public exposure and no CA paperwork. The alternative — installing a self-signed root as a
trusted profile on iOS — works but is enough friction to sink the MVP's first impression.

The QR (and its fingerprint) stays useful in both cases: it still carries the host address,
pairing token and protocol version, and pinning a Tailscale-issued cert is harmless.

### 7.2 Background push — resolved, and it does *not* force a server

The premise this section used to carry ("push-while-closed is the one thing that forces us
to run a service") is wrong, and correcting it removes the largest open risk in the
document.

The first half still holds: a VPN keeps the *socket* alive, but the mobile OS still
suspends the *app*, so "a tell arrived while your phone was asleep and Scrye wasn't open"
genuinely cannot be delivered over LAN or VPN alone. It has to go through APNs/FCM. What
does not follow is that *we* must operate the thing that talks to APNs/FCM.

**Decision: use Web Push with the desktop as the application server.** The phone's push
subscription returns an endpoint URL hosted by the browser vendor's push service. Whoever
holds the VAPID key pair POSTs the encrypted payload to that endpoint — and that
"whoever" is `Scrye.App` itself. Requirements on our side amount to: generate a VAPID
keypair once, store the subscription the phone hands over at pairing, and make outbound
HTTPS POSTs. The desktop already has outbound internet. There is no relay, no user
account, no uptime obligation, and no bandwidth cost.

The privacy story is better than the relay's too: Web Push payloads are encrypted with
keys the push service never possesses, so Apple and Google forward ciphertext. Our GDPR
surface for other people's private MUD chat drops to approximately nothing.

Two constraints this places on the rest of the design:

- **The browser companion must be an installable PWA**, not just a page. On iOS, Web Push
  is only available to a PWA that the user has added to the home screen. Manifest +
  service worker therefore move into the step-3 MVP (§10) rather than being a later polish
  item.
- **It needs a browser-trusted certificate** (§7.1) — service workers require a secure
  context. This is the concrete reason §5 promotes Tailscale to the primary tier.

A **webhook notifier** is the sensible companion feature and a good fallback: let the user
point Scrye at their own ntfy / Pushover / Discord / Telegram endpoint and POST to it.
It is perhaps a day of work, needs no PWA and no VAPID, and covers users who never install
to the home screen. Shape it as a scripting-visible `scrye.notify(...)` so plugins can
raise notifications too. The tradeoff is that the payload passes through a third party,
which is acceptable precisely because the *user* chose it.

Notifications the desktop could raise — delivered live over the socket while the app is
connected, and via Web Push (or webhook) when it is not: tell received, character
disconnected, low health, script paused, route completed, login finished.

### 7.3 "Send commands" must not imply "run scripts"

The permission table above promises `✓ Send commands` and `✗ Edit scripts` are separable.
In the current code they are not, and §4's rule ("phone input lands on the same hook as
typed input") is what fuses them. `WorldViewModel.Submit()` dispatches on the text before
it ever reaches the MUD:

```csharp
if (text == "mipstart") { _session.StartMip(); return; }
if (TryClientCommand(text)) { ... }                              // "." client commands / sequences
if (text.StartsWith('/') && text.Length > 1) { _session.RunScript(text[1..]); return; }
```

So a paired phone holding nothing but "send commands" can type
`/world.AddAlias("x", "*", "...")` — or anything else — and get **arbitrary Lua executing
on the session loop**. A stolen phone, or a device paired for a friend to watch a fight,
becomes full scripting access to the client.

**Decision: gate at the entry point, not in the UI.** `SubmitText(text, CommandOrigin)`
(§4) takes an origin carrying both the source and its capabilities. A `/` command from an
origin without `MayRunScripts` is rejected — before any echo, so a refused command leaves no
trace of having half-run — and the rejection is an error frame back to the phone, not a
silent drop. `CommandOrigin.Companion()` defaults to `mayRunScripts: false`; the per-device
lookup belongs to the server, which knows which device sent the frame, and the view model
only enforces.

The rule itself lives in `Scrye.Core.Automation.CommandPrivilege`, not in the view model:
it is small, security-relevant, and needs exactly one definition that every entry point
shares — and in `Scrye.Core` it is unit-testable without any UI.

**Only `/` is gated. `.` client commands are not.** An earlier draft lumped them together as
"the scripting surface"; that is wrong. `/…` runs arbitrary Lua on the session loop. `.…`
fires *sequences* — `.walk`, `.seq`, `.stop`, `.log`, `.tts` — which are command lists this
desktop already authored, take no arbitrary paths, and execute no user-supplied code.
Firing a walk route from a phone is one of the better reasons to have a companion at all,
so gating it would be a self-inflicted wound. The privileged set is exactly one prefix.

Two things the gate deliberately does not do. It does not filter on the *content* of
ordinary commands — aliases, triggers and macros still expand normally, which is the whole
point of §4. And it does not live in the input box or anywhere else on the UI side.

### 7.4 MXP links must NOT be prefix-dispatched

A companion note in an earlier draft suggested routing MXP link taps through the same
`SubmitText` path "so a hostile MUD cannot smuggle a `/` command onto a phone." That
recommendation was backwards and is retracted here, because following it would have
*created* the vulnerability it was trying to prevent.

`HandleCommandLink` currently sends the link's action straight to `MudSession.Submit` as
literal text. It never touches the `/` or `.` dispatch. That is already the safe behaviour:
an MXP `<SEND>` whose action is `/world.AddAlias(...)` is transmitted to the MUD as the
string `/world.AddAlias(...)`, not executed. Routing it through the full pipeline is exactly
what would have executed it.

The principle: **prefix dispatch applies to text a human entered, never to text the MUD
supplied.** A companion server handling a link tap must send it raw for the same reason.
(The `SEND PROMPT` variant, which puts the action in the input box rather than sending it,
stays safe by a different route — the user sees the text and has to press Enter themselves.)

### 7.5 Tailnet identity beats a shared token

Shipped 2026-08-02, replacing token entry as the normal path.

`tailscale serve` **strips any client-supplied identity headers and sets its own**, so a
`Tailscale-User-Login` arriving at the companion server genuinely came from the proxy and
names the tailnet user who made the request. Scrye reads its own login from
`tailscale status` at startup and allows exactly that one.

The practical problem this solved was not theoretical: a 43-character token, regenerated on
every server start, had to be *typed on a phone* — there is no clipboard between a Windows
PC and an iPhone. That pressure runs one way, toward shorter and weaker tokens. Identity
headers remove the credential from the interaction entirely.

The token survives as a fallback for loopback testing and for setups without Tailscale.
`GET /whoami` tells the client which applies, because a failed WebSocket handshake exposes
no status code to script — without it the app would have to prompt for a credential it may
not need.

**The honest caveat:** the header is only trustworthy for requests that actually traversed
the proxy, and the server cannot distinguish those from another local process connecting to
the loopback port with a forged header. This is a smaller hole than it appears — anything
running as the user could read the token, read process memory, or drive the desktop client
directly — but it is why this is an explicit allow-list of one login rather than "trust any
Tailscale header".

**Consequence for step 4.** Per-device pairing is now less urgent than when this document
was written, and should be designed as *the answer for devices that are not on the tailnet*
rather than as the primary mechanism.

---

## 8. Frontend: browser first, native maybe later

### 8.1 An installable PWA is the MVP

Because Scrye is .NET, the desktop can host Kestrel + a WebSocket endpoint **in-process**
trivially, and serve a small single-page app. A browser frontend:

- validates the entire protocol *and* the HUD-spec-streaming idea end to end,
- runs on both iPhone and Android with no app-store step,
- gives a genuinely usable phone UI in days, not months.

The Telnet restriction never applies to the browser, because the *PC* makes the Telnet
connection; the browser only speaks HTTPS/WebSocket to the PC. **Live in the browser
version for a few weeks before deciding native is worth it.**

Build it as an **installable PWA from the start** — a web app manifest and a service
worker, served over the browser-trusted cert from §7.1. That is a small delta over a plain
SPA and it buys a home-screen icon, standalone display without Safari chrome, an offline
shell, and — per §7.2 — Web Push. On iOS none of those are available to a page that has
not been added to the home screen, so this is not a polish step to defer.

### 8.2 If/when native: Avalonia mobile, not MAUI

The instinct is that MAUI suits a thin companion. For *this* codebase the opposite holds:
Scrye already has custom Avalonia HUD controls (`BarListView`, the dim-ramp gauge,
`colorgrid`). Avalonia-mobile lets the companion **reuse those renderers** and share the
protocol DTOs; MAUI would mean rebuilding every one of them. So Avalonia-mobile is the
cheaper native path here — but only after the browser version has proven what native
actually needs to add.

The PWA route (§7.2, §8.1) already covers notifications and offline cache, which narrows
the honest remaining candidates to HUD-control reuse and **text input**. Expect the input
box to be the deciding factor: mobile Safari gives a MUD command line viewport-resize jank,
autocorrect fighting MUD syntax, no key repeat, no modifier keys, and no reliable
up-arrow history. Watch that specifically while living with the PWA — it is a better
signal that native is worth the cost than layout or notifications will ever be.

### 8.3 Mobile UX the structured feed unlocks

Custom command pads (`N S E W U D`, `Attack / Defend / Flee / Heal`), swipe actions
(up = previous command, left = map, right = chat), a **chat-only screen** (tells/says/
channels with the room and combat spam filtered out), and voice-to-command ("go north",
"tell Erik I'll be there soon") — all of which are just different views over the same
structured stream, with the desktop still deciding how aliases and command processing work.

Reference mobile layout:

```
┌────────────────────────────┐
│ Eldran       HP 82%    ⋮   │
├────────────────────────────┤
│  MUD output                │
├────────────────────────────┤
│ > command                  │
├────────────────────────────┤
│ N  S  E  W  Look  Attack   │
├────────────────────────────┤
│ Output  Chat  Map  Status  │
└────────────────────────────┘
```

Tablets can use a wider two-panel layout.

---

## 9. Project structure

Resist a big reorg of the existing assemblies. The genuinely new pieces are only two:

```
Scrye.Companion.Protocol   NEW — shared DTO records (§3.2); referenced by app and phone
Scrye.Companion.Server     NEW — Kestrel + WebSocket host; runs INSIDE Scrye.App
Scrye.App                  existing — hosts the server, taps output/state/HUD/commands
Scrye.Core / Scrye.Scripting   existing — unchanged
Scrye.Companion.Mobile     LATER — Avalonia-mobile app (only if native is pursued)
```

Hosting the server inside `Scrye.App` (rather than a separate process) keeps it next to
the `WorldViewModel`/`MainWindowViewModel` state it taps, with no IPC.

---

## 10. Recommended build order

0. ~~**Seam prep in the existing app**~~ — **done 2026-08-02.** `SubmitText(text,
   CommandOrigin)` extracted from the private `Submit()` with the §7.3 gate in place, the
   rule itself in `Scrye.Core.Automation.CommandPrivilege`; `ScrollbackBuffer` gained
   `BaseSequence` / `NextSequence` / `SequenceAt` / `TryGetIndex` / `CanReplayFrom` /
   `LinesAfter` (§6). `Clear()` advances the base rather than resetting, so a stale client
   is forced to snapshot instead of being served the wrong lines.
1. ~~**Companion protocol**~~ — **done 2026-08-02.** `src/Scrye.Companion.Protocol`:
   the DTOs (§3.2), `OutputBatchBuilder` (per-frame style interning), and `CompanionJson`
   (camelCase, string enums, nulls omitted). References `Scrye.Core`, never `Scrye.App`.
2. ~~**In-app companion server**~~ — **done 2026-08-02.** `src/Scrye.Companion.Server`:
   Kestrel + WebSocket inside `Scrye.App`, multi-subscriber, per-connection bounded outbound
   queues, the §7.3 gate at the boundary, replay-or-snapshot resume. The app side reaches it
   only through `ICompanionSessionSource`, which owns all UI-thread marshalling (§4.1).
   Started with the temporary `.companion` client command. First-cut posture is **loopback +
   one shared token, plain HTTP** — `127.0.0.1` is a browser secure context, so a PWA works
   for bring-up without a certificate; `permessage-deflate` and TLS come with step 6.
3. ~~**Browser MVP, as an installable PWA**~~ — **done 2026-08-02.** Installed to an iPhone
   home screen and in use. Output pane with ANSI and tappable MXP links, pinned prompt strip,
   command line with history, directional/action pad, vitals meters bound to state paths;
   manifest, service worker and icon served from the companion host. Resumes from its last
   sequence on reconnect and on `visibilitychange`, so backgrounding the phone costs nothing.
   The protocol scope from the earlier pass remains at `/debug`.
   *Note: on iOS only **Safari** can install a PWA to the home screen — every browser there
   uses WebKit and Apple reserves installation for Safari. Chrome cannot do it at all.*
4. **Pairing & permissions** — QR pairing (§7.1), per-device keys, revocation, the
   paired-devices list, and capture of the phone's Web Push subscription at pairing time.
5. ~~**Resume & snapshot**~~ — **done 2026-08-02**, inside steps 0 and 2 rather than as a
   separate pass. Sequence numbers on `ScrollbackBuffer`, `CanReplayFrom` deciding replay
   versus snapshot, and the client resuming both on reconnect and on `visibilitychange` —
   so backgrounding the phone costs nothing.
6. ~~**Trusted cert + remote access**~~ — **done 2026-08-02.** `tailscale serve --bg
   --https=443 http://127.0.0.1:4747` puts Tailscale's TLS proxy in front of the
   loopback-bound server. Verified working from an iPhone over the tailnet.
   Decisive detail: the proxy **renews the certificate itself**, whereas `tailscale cert`
   makes renewal the user's problem and Let's Encrypt certs lapse after 90 days — a manual
   cert would break the phone on a forgotten Tuesday. Scrye stays loopback-bound and holds
   no certificate code at all. `.companion tailscale` reports node state and prints the
   command. Walkthrough: `docs/Scrye-Companion-Setup.md`.
7. ~~**Notifications**~~ — **done 2026-08-02.** `src/Scrye.Companion.Server/Push`:
   RFC 8291 encryption verified against the RFC's own test vector, VAPID identity persisted
   next to the profiles, subscription storage with 404/410 pruning. Hooked to the existing
   `MudSession.NotifyRequested`, so triggers already flagged Notify reach the phone with no
   new configuration; `3s-chat` gained always-on tells plus per-channel opt-in.
   `.companion notify` audits what will fire.
   *The webhook fallback (ntfy/Pushover/etc.) was not built — Web Push made it unnecessary
   for the tailnet case, and it remains the answer if a non-PWA client ever needs one.*
8. *(Optional)* **Native app** — Avalonia-mobile, reusing DTOs and HUD controls (§8.2).

Built alongside the numbered steps, and worth listing because they are not in it: HUD panel
rendering on the phone with `hud.action` (§2's headline claim, now real), the chat view over
`output.pane`, tailnet identity authentication (§7.5), and `permessage-deflate`.

Steps 1–3 (with 6) are the whole idea, working. Everything after hardens or extends it,
and each is independently shippable. The former step 8, "minimal push relay", is deleted:
§7.2 shows there is nothing left for it to do.

---

## 11. Decisions

*Resolved 2026-08-02. The first two were open questions in the previous revision; the
answer to 11.1 turned out to dissolve the question rather than pick a side, and that
cascaded into §5, §7.1, §7.2, §8 and §10.*

### 11.1 Push-while-closed — **yes, and no hosted component**

The question was framed as a binary: accept no background push, or build a relay and take
on accounts, uptime and GDPR liability. It is a false binary. **Web Push with `Scrye.App`
itself as the application server** delivers push-while-closed with nothing for us to
operate — see §7.2 for the mechanism and §7.1/§5 for the certificate consequences.

Cost of the decision: the browser MVP must be an installable PWA (manifest + service
worker), and it needs a browser-trusted certificate, which is why Tailscale is promoted to
the primary transport tier. Both are cheap; neither involves running a service. A webhook
notifier (`scrye.notify(...)` → ntfy / Pushover / Discord / Telegram) ships alongside as
the fallback for users who never install to the home screen.

### 11.2 Native at all — **not yet; re-evaluate on one specific signal**

Unchanged in direction, sharper in criterion. Live with the PWA first. Notifications and
offline cache are no longer arguments for native, since §7.2 covers both. The two honest
remaining arguments are reuse of the existing Avalonia HUD controls and, above all,
**text input quality** — see §8.2. Treat "the command line on mobile Safari is
unacceptable in daily play" as the trigger to start step 8, and treat nothing else as one.

### 11.3 Concurrent devices — **single *user* in v1, multi-*subscriber* from day one**

Distinguish the two. The v1 *scope* is one person, no accounts, per-device keys as
described in §7 — that stays. But even one person is realistically a phone, a tablet and a
desktop browser at once, so the server must not bake in a single-connection assumption.
From the first commit of `Scrye.Companion.Server`:

- per-connection sequence cursors, not one global cursor;
- broadcast fan-out of `output.line`, `state.update` and `hud.*` to all subscribers;
- commands echoed to **every** connected client, not just the sender — which follows
  naturally from §4's rule that phone input lands on the same hook as typed input;
- the paired-devices list (§7) is already per-device, so nothing new is needed there.

This is nearly free now and a rewrite later. What is deferred indefinitely is multi-*user*:
separate accounts, per-user permission sets, and anything that implies identity beyond
"this device is paired to this desktop."

### 11.4 Closed by reading the code (2026-08-02)

Both questions left open in the previous revision turned out to be answered by the engine
already:

- **Ring buffer size for resume** — moot. `ScrollbackBuffer` already holds 50,000 lines;
  resume reads it directly rather than adding a second buffer (§6). What replaced this as
  a real concern is the index-vs-sequence trap after trimming, also §6.
- **Palette handover** — moot, and the underlying idea was wrong. Colour is resolved to
  24-bit `Rgb` at parse time and the palette index is discarded, so there is no palette to
  hand over and no way to re-theme old lines even on the desktop (§3.3).

Three new decisions were taken in the same pass and are recorded in place rather than
here: the wire format switches to batched frames with a per-frame style table (§3.1–3.2),
companion input gets its own entry point with a scripting-permission gate (§7.3), and the
server owns an outbound queue rather than touching `StateStore` or `Scrollback` from socket
threads (§4.1).

Nothing is currently blocking step 1.
