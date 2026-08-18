# Scrye — User & Plugin Guide

Scrye is a cross‑platform MUD client (C# / .NET 10, Avalonia UI). It connects to text MUDs, renders their ANSI/MXP output, and gives you the usual automation — triggers, aliases, timers, macros, highlights — plus a profile system, themeable UI, capture panes, the MIP feed, a Lua/JavaScript **plugin** system that can add commands and live **HUD panels**, and a **mobile companion** that mirrors all of it to your phone.

This guide has two parts:

1. **Using Scrye** — for everyone.
2. **Writing plugins** — for plugin authors, with a full `scrye.*` API reference.

---

# Part 1 — Using Scrye

## Connecting to a world

A "world" is a MUD connection. You can connect two ways:

- **Quick connect** — type a host and port and go. This is session‑only: nothing is saved.
- **Saved profiles** — a world defined by a MUD, and optionally an Account and a Character under it. Saved worlds remember their settings and reconnect the same way each time.

Each connected world gets its own tab with an output pane and a command line.

## The main window

- **Output pane** — the MUD's text, with ANSI colors, MXP links, and clickable command links. It has its own scrollback; scroll up to read history, and it snaps back to the bottom on new output.
  Drag to select, then **Ctrl+C** or right-click to copy. The menu also offers **Copy as ANSI** and **Copy as HTML**, which keep the colours — useful for pasting a fight into a forum or a bug report. **Ctrl+A** selects the whole scrollback.
- **Command line** — type a command and press **Enter** to send it. Handy keys:
  - **Up / Down** — walk back and forth through command history.
  - **Tab** — complete the word under the caret from words seen in the output.
  - **Ctrl+F** — open the find bar to search the scrollback.
  - **Esc** — clear the input.
- **F11** — fullscreen on and off.

## Several commands at once

A semicolon separates commands, so one line can be several:

```
vtrade refine smithy transfer all;vtrade refine smelter transfer all;vtrade refine all fill
```

Each part takes its own trip through the alias pipeline, so `n;gg;s` fires the `gg` alias in
the middle exactly as if you had typed it on its own. Spaces around a separator are trimmed
and a trailing `;` is ignored, so `n; s;` is just two commands.

To send a real semicolon, double it: `say I went there;; it was fun` goes out as one command
with one `;` in it.

Two things never split, because a semicolon already means something there: `/` script-console
lines, where `;` separates Lua statements, and `.walk`/`.seq` lines, where the sequence parser
owns it. Neither does anything a trigger, timer or plugin sends — the separator is for what a
person types, and a plugin sending `say a;b` means one command. Clickable links the *MUD*
sends (MXP `<SEND>`) are also taken literally, for the same reason they never reach the script
console: one click should be one command.

## Client commands

A handful of commands are handled by Scrye itself rather than sent to the MUD. They all start
with a dot, and they're intercepted before plugins see them, so a plugin can't shadow one.

| Command | What it does |
|---|---|
| `.log` | **Start logging this session's output to a file.** Everything that appears in the output pane, as plain text. |
| `.log html` | Same, but a self-contained HTML file that keeps the colours. |
| `.log off` | Stop logging. A bare `.log` while already logging also stops it. |
| `.walk north;north;east x3;wait 2` | Run an ad-hoc walk. `x3` repeats a step, `wait N` pauses. |
| `.seq <name>` | Run a saved sequence — or pick it from the **sequence strip** above the input line and press Run. |
| `.stop` · `.pause` · `.resume` | Control the running walk or sequence. The same three buttons appear in the sequence strip while one is running. |
| `.all <command>` | Send one command to **every** connected world. |
| `.idle` | Show or set the idle guard (`.idle on`, `.idle off`, `.idle 300`, `.idle 10m`). The **Idle** toggle in the bottom bar switches it; the command is how you set the limit. |
| `.tts` | Toggle text-to-speech (Windows only). |
| `.ts` / `.timestamps` | Toggle the HH:mm:ss gutter in the output and capture panes (or the **⏱ Time** toggle in the bottom bar). |
| `.companion` | The mobile companion — see its own section below. |
| `.mip` | Audit the MIP feed for structural drift — see below. |
| `.mip fields` | List what this character actually receives — every field, key and frame type. |

Separately, anything starting with **`/`** is Lua rather than a MUD command — see
[The script console](#the-script-console).

Logs are written to the logs folder (`%APPDATA%/Scrye/logs`, or `~/.config/Scrye/logs` on
Linux and macOS). The log captures **displayed output only** — it is a transcript of what you
saw, so out-of-band protocol traffic like MIP and GMCP is not in it. Commands you send to the
MUD *are* recorded, as `> command` lines, the same way they are echoed to the screen; client
`.` commands and `/` script lines are not, because they never reach the session.

### Logging every session automatically

Rather than remembering `.log`, tick **Log every session** in a profile's settings (with
**as HTML** beside it if you want the colours kept). It's a normal cascade setting, so setting
it on a **Character** is what makes it per-character — and that's the useful place for it,
because the file is named after the character:

```
2026-08-14-Bjorn.log
2026-08-14-Bjorn-2.log     <- a second session the same day
2026-08-14-Freya.html
```

Date first, so a folder sorted by name is also sorted by day. A second session on the same day
gets `-2`, `-3` and so on rather than overwriting the first.

Two things it deliberately does: an **auto-reconnect keeps writing to the same file** instead of
starting a fragment per dropped connection, and **`.log off` stays off** for the rest of the
session — a blip on the link won't turn logging back on after you've explicitly stopped it.
A later `.log` re-arms both.

## The script console

Anything you type starting with **`/`** is run as Lua on this world's session loop instead of
being sent to the MUD. It's the quickest way to poke at a world without opening Settings:

```
/world.Send("look")
/world.Note("hello from Lua")
/world.SetVariable("tank", "Bjorn")
/world.AddAlias("greet", "hi *", "say hello %1")
```

The `world` table is small and deliberate — these eight functions, nothing else:

| Call | What it does |
|---|---|
| `world.Send(text)` | Send a command to the MUD, exactly as if you'd typed it. |
| `world.Note(text)` | Print a local line in the output pane. Never reaches the MUD. |
| `world.GetVariable(name)` | Read a variable; `nil` when unset. |
| `world.SetVariable(name, value)` | Write a variable. |
| `world.AddTrigger(name, pattern, send)` | Add a trigger that sends a command on a match. |
| `world.AddAlias(name, pattern, send)` | Add an alias. |
| `world.DeleteTrigger(name)` | Remove one by name; returns `true` if it existed. |
| `world.DeleteAlias(name)` | Same for aliases. |

Rules added this way are **session-only** — they vanish on restart. To keep one, put it in
Settings, where it becomes part of the profile cascade.

It runs on the session loop, so a script never executes concurrently with trigger processing,
and it's **sandboxed exactly like a plugin**: native Lua 5.4 with the standard library, but no
`io`, no `os.execute`, no `require`, no `load`, no `debug`. A runaway loop hits the same
dispatch budget a plugin does and is aborted rather than freezing the client.

> **This is the one privileged input.** A command from your phone can do everything else —
> walk a route, fire a sequence, send to the MUD — but `/` script is refused unless that
> device was explicitly granted scripting, which it is not by default. That's why it's the
> only prefix checked before anything is echoed: a rejected command leaves no trace of having
> half-run. If you're wondering why `.walk` isn't gated the same way, it's because a sequence
> is a command list this desktop already authored, whereas `/` is arbitrary code.

## Profiles and the cascade

Settings resolve through a four‑layer cascade, from most general to most specific:

**Global → MUD → Account → Character**

Deeper layers win for single values (theme, font), and collections (triggers, aliases, macros, variables) are merged across layers. This means you can set something once globally and override it for one character. The **Global** layer is edited in **Settings**; MUD/Account/Character layers are edited from the world/profile UI.

## Automation

- **Triggers** — match lines of MUD output (plain text or regex) and react: send commands and/or run script. A trigger can send **multiple commands** — separate them so each goes on its own line.
- **Aliases** — match what *you* type and rewrite/act on it. A matching alias consumes your input.
- **Timers** — run on an interval.
- **Sequences** — a named list of commands you can fire together (e.g. a walk).
- **Highlights** — recolor matching text in the output without changing anything else.
- **Macros / keybindings** — bind a key (e.g. `F1`, `Ctrl+K`, `NumPad1`) to send a command or run an action.

These are all editable in Settings (Global) or per world/character.

### Finding things in a long list

Every rule list in Settings and in a world's editor is **sorted A–Z** and has a **filter box** above it that matches on name, pattern and group as you type.

Triggers, aliases and timers also have a **Group** field, and the list draws one collapsible heading per group with the rules sorted inside it — click a heading to fold it away. Anything with no group set collects under *(no group)* at the bottom. Groups are just a label you choose, so use whatever divisions you actually think in: `combat`, `travel`, `guild`. They earn their keep twice over, because a plugin or script can switch a whole group on and off at once rather than hunting down each rule.

Two things worth knowing about the ordering. It is **display only** — the file keeps the order you added things in, and match order has never come from this list anyway: the engine sorts rules by their **Sequence** number (lower runs first), which is the field to reach for when one trigger genuinely has to beat another. And the list re-sorts when you *leave* a rule rather than as you type, so renaming something doesn't make it jump around under the cursor mid-edit.

## Appearance

In **Settings → Appearance**:

- **Theme** — several dark/light color schemes with different accents. The game **output pane stays near‑black in every scheme** so the MUD's own colors always read correctly — even under Light. The **Void (black on black)** scheme takes that to its conclusion: pure-black output *and* pure-black panels, with the borders carrying the structure.
- **MUD colors (ANSI palette)** — choose how the MUD's ANSI color codes are painted:
  - **Modern (xterm)** — the softer xterm/VGA palette (default).
  - **MUSHclient (classic)** — MUSHclient's default palette (pure‑primary bright colors, olive yellow), if you want it to look exactly like MUSHclient. Applies to new output.
- **Font** — a dropdown of the **monospaced** fonts installed on your machine, with a live preview. MUD output needs a fixed‑width font so columns line up; the picker only lists monospaced fonts so you can't break alignment by accident. There's also an "Advanced" box for a custom comma‑separated fallback chain.
- **Font size**.

Scrye also lifts any near‑black text the MUD sends so it stays legible on the dark background — bright colors are left untouched.

## HUD panels

Plugins can contribute **HUD panels** — small floating widgets (status bars, gauges, maps, buttons) that sit over the output and stay in sync with game state. You can **drag panels** by their title to reposition them so they don't overlap, and **resize** them by dragging the grip in the bottom-right corner — a panel smaller than its content scrolls, and **double-clicking the grip** snaps it back to auto-size. Both dragging and resizing **snap to alignment** — the edges of the output area and the edges of the other panels — so stacks line up without pixel-hunting; **hold Alt** while dragging for free, pixel-exact placement. Positions and sizes are remembered per world.

## Chat from your other worlds

With several worlds open, a tell to a character on one MUD is easy to miss while you're
playing another. Scrye relays it: a chat line from a world you're **not** looking at is drawn
in whichever tab **is** in front, prefixed with the world it came from.

```
[Aardwolf] Bob: you around?          <- a tell, from the Aardwolf tab
[Aardwolf/Guild] raid in 10          <- a channel, when you've asked for that channel
```

It also lands in the **Chats pane** (`3s-chat`) of the tab you're in, so it doesn't scroll away
in the middle of a fight — timestamped and labelled with the world it came from, alongside this
world's own chat.

By default only **tells** relay — channel chatter from a MUD you aren't reading is noise, but
a tell is worth interrupting you. To change it, set **Relay chat to the front tab** in the
world's settings: a comma-separated list of channel names (`Tell, Guild`), `*` for everything,
or `none` for nothing. It's a normal cascade setting, so putting it on the **Global** layer
sets the default for every world, and a single character can then override it.

It's a property of the world the chat comes *from* — "what may this world interrupt me with" —
not of the tab you happen to be in.

Three things it deliberately does not do:

- **You can't reply from the other tab.** What you type goes to the world you're in. Routing a
  reply somewhere else based on the last line you happened to see is how a private message
  ends up on the wrong MUD. Switch tabs to answer.
- **A relayed line is not MUD output.** It never reaches the current world's triggers or
  session transcript — a foreign line tripping a local trigger and firing automation would be a
  bad surprise, and your Aardwolf log should not contain 3Scapes chat. It reaches the Chats pane
  only because `3s-chat` asks for it explicitly, through a hook (`scrye.onRelay`) kept separate
  from the one carrying this world's own chat: the pane shows it, but it isn't notified a second
  time, isn't written to this world's chat log, and isn't replayed into this world's history
  next session.
- **Your own outgoing tells don't relay.** You know what you just sent.

## MXP — what the server can do

MXP is markup a MUD can send inline: clickable commands, links, colours, and a few things that
reach further into the client. Scrye turns it on only when the MUD negotiates it (telnet option
91), so nothing changes on a MUD that doesn't use it.

The whole design rests on **secure mode**. A MUD marks a line secure before sending anything
powerful; on an ordinary line those tags are ignored. That's what stops another player's `say`
from containing a clickable "quit" — Scrye refuses to make a link out of markup the server
didn't vouch for, and in *locked* mode it doesn't even look for tags.

| What the MUD sends | What you get |
|---|---|
| `<SEND href="kill troll" hint="attack it">troll</SEND>` | **Click the word** to send the command; the hint shows as a tooltip. With `PROMPT`, it goes into your input box instead of being sent. |
| `<A href="https://…">site</A>` | A link that opens in your browser. |
| `<B> <I> <U> <S>` | Bold, italic, underline, strikethrough. |
| `<COLOR fore back>`, `<FONT color=>` | Inline colour, independent of ANSI. |
| `<VAR hp>85</VAR>` | Sets **`${mxp.hp}`** — usable in triggers, aliases, timers and HUD panels. |
| `<DEST chat>…</DEST>` | Routes those lines into the **capture pane** named `chat`, exactly as a trigger's capture would. |
| `<GAUGE hp max=maxhp caption="Health">` | Publishes `mxp.gauge.hp.value` / `.max` / `.caption` to the state store, so a HUD gauge or `scrye.watch` can bind it. |
| `<!ENTITY …>` and `<!ELEMENT …>` | The MUD's own shorthand, expanded to the tags above. |

**Server variables are namespaced.** A `<VAR hp>` becomes `${mxp.hp}`, never `${hp}`. A MUD
cannot redefine a variable your own aliases depend on — if you set `targ` with an alias, no
server can touch it.

`<IMAGE>` and `<SOUND>` are deliberately not supported: both mean fetching or playing something a
remote server names, which is a poor trade in a text client. MSP already covers sound.

If you write MUD code and want to add MXP to your game, two things matter more than the tag list.
Emit `<SEND>` **inline** rather than defining custom elements — Scrye supports definitions, but
support varies across clients. And **escape anything a player can influence** (`&` `<` `>` become
`&amp;` `&lt;` `&gt;`), or a player-chosen name becomes markup on someone else's screen.

## Capture panes

A **capture pane** is a separate scrolling pane that collects specific lines — for example, all chat channels and tells routed into one "Chats" pane. Plugins (and triggers) can route lines into named panes. Show and hide the pane area with the **Panes** toggle in the bottom bar.

Panes appear on their own. A plugin that captures into one declares it in its manifest, so enabling the plugin creates the pane there and then — you don't have to open the Panes area and wait for something to arrive. A pane a trigger routes into is created the first time a line lands in it. Either way the layout is remembered per world, so it comes back where you left it.

**Close a pane and it stays closed** — including across restarts, even if a plugin declares it. It only returns if a line is actually routed into it, at which point there's something in it worth reading.

**Right-click a pane's tab** for the things you can't do any other way:

- **Move to bottom** / **Move to right side** — dock the pane along the bottom or down the right edge.
- **Float as window** — pop it out into its own window, handy on a second monitor.
- **Close pane**.

A floated pane can be re-docked from the same menu on its tab once it's back.

## Plugins

Plugins add commands and HUD panels. Manage them in the **Plugins** panel for a world:

- Plugins are **opt‑in per character** — enabling one for a character doesn't add it to every character.
- Each plugin can be **enabled / disabled / reloaded / removed**.
- **Reload** re‑reads the plugin's script from disk, so you can edit a Lua plugin and reload it live — no restart needed (this works for script‑only changes; changes to Scrye itself need a rebuild).
- **New plugin** scaffolds a working `plugin.json` and `main.lua` in your user plugins folder (named `my-plugin`, `my-plugin-2`, …) — the quickest way to start one without hand-writing a manifest. **Open folder** opens that folder, and **↻** rescans the disk for anything added or removed outside Scrye.

## Mobile companion

Scrye can serve a small web app to your phone so you can read output, send commands, watch your HUD panels and get push notifications while you're away from the PC. The desktop stays in charge: it holds the connection, runs the triggers and plugins, and the phone is just another view of it. Close the phone, come back an hour later, and it resumes where it left off — or takes a fresh snapshot if it's been away too long.

### Turning it on

Click **📱 Companion** in the bottom bar. The panel shows whether the server is running, a **QR code** to point your phone's camera at, the access token, and everything in this world that can raise a notification — and lets you change it. Triggers have a **Notify tick box** (the change saves to the connected character's profile and applies immediately, no reconnect); plugin settings appear underneath with live switches, and where a plugin reports a list — the chat plugin's watched names and notifying channels — you can add entries and remove them with the ✕. Start and stop the server from there.

Two triggers can't be edited from the panel: an **unnamed** one inherited from a shallower profile layer, because the cascade merges rules by name and there'd be nothing to attach the override to, and any trigger in a **quick-connect** world, which has no profile to save into. Both say so on the row.

The token is deliberately only shown in the panel. It used to be printed into the output pane, which meant session logging wrote a live credential to disk — the panel avoids that entirely.

The same things are available from the command line, which is quicker mid-fight:

| Command | What it does |
|---|---|
| `.companion` | Start the server and open the panel. Prints the URL and this world's session id. |
| `.companion status` | Is it running, where, how many phones are registered for notifications. |
| `.companion tailscale` | How to reach it from outside the house — prints the exact `tailscale serve` command to run. |
| `.companion notify` | List everything in this world that can raise a notification. |
| `.companion notify test` | Send a test notification to every registered phone. |
| `.companion off` | Stop the server. |

There's one more diagnostic worth knowing:

| Command | What it does |
|---|---|
| `.mip` | Audit the MIP viking feed's **structure** against what the parsers expect, and report anything that looks like it has drifted. |
| `.mip fields` | List every MIP field, feed key and frame type this character has received, each with a live sample. |
| `.mip fields save` | The same as a markdown file in the log folder, to hand to whoever is writing the plugin. |

The feeds are positional — `BATTLE` is eleven pipe-separated fields, and each unit record is six or nine comma-separated ones depending on whether the unit is in reserve or fielded — so if the server inserts a field, nothing errors. The parser just reads the wrong slot from then on and the numbers quietly go wrong. `.mip` watches every key that arrives and flags two things: a key whose layout no longer matches a **recorded expectation**, and a key whose shape **changes mid-session**, which is drift no matter what any table claims.

Two honest limits. It can only speak for keys the server actually sent, so run it after visiting whatever exercises the feeds (and with the right `vtoggle` flags on). And only a handful of keys have a recorded expectation — the ones whose layout was read directly off a parser. Everything else is listed as "no recorded expectation" with its observed shape, which still catches a later change but has never been checked against anything.

### Finding out what a guild sends — `.mip fields`

Before you can write a plugin for a guild, you have to know what that guild's characters actually receive, and `.mip` won't tell you: it audits *structure*, for keys something already parses. `.mip fields` answers the earlier question, listing three different things because MIP carries three:

- **Vitals (FFF)** — the fixed per-character slots every guild fills in: `hp`/`sp`/`gp1`/`gp2` and their maxima, plus `gline1`/`gline2`, the guild's own status lines as free text. Same eight numbers whatever you play; what they *mean* is the guild's business. A field the server never sent shows as `(not sent)` rather than blank, which is itself a finding — Vikings don't use SP, so it arrives as nonsense or not at all.
- **Feed keys (BBE)** — key/value pairs, where a guild puts whatever it likes. Read from a plugin as `scrye.getState("vik.<key>")`, lower-cased. The `vik.` prefix is historical: BBE is the generic carrier and the Viking guild was simply first to use it, so *every* guild's keys land there.
- **Tags** — the frame types themselves, listed whether or not Scrye decodes them. A tag with no decoder is the interesting case, and its raw payload is parked in state as **`mip.<tag>`** — so a plugin can use a new guild's feed the day the MUD starts sending it, without waiting for the client to learn its structure.

Every row carries a live sample, because a shape fingerprint tells you a value has four pipe-separated fields and a sample tells you what they mean.

### Parsing a guild's status lines — `character.gline1.raw`

Not every guild uses BBE. Measured on 3Scapes: a Viking character receives 238 feed keys, an elemental character receives **none** — everything that guild knows about itself is in its two gline strings. So for a gline-only guild, parsing those isn't one option, it's the only one.

They arrive colour-tagged, and the tags are the field boundaries:

```
character.gline1        Emit : 16  Form: Time(1550)  Rating: 745
character.gline1.raw    <yEmit> : <r16>  <gForm>: <cTime>(<r1550>)  <cRating>: <r745>
```

Labels in one colour, values in another. Pull every `<r…>` run out of the raw line and you have `16`, `1550`, `745` unambiguously; the stripped line offers only whitespace to guess at. Scrye publishes both — the plain one is what you show a player, the raw one is what you parse.

A guild that uses no colour tags loses nothing: the viking glines delimit with brackets (`H[7044|7053] S[5319|5319]`), so their raw and plain forms are identical.

Both appear in `.mip fields`, so you can see the parseable form without going looking for it.

`.mip fields save` writes the same report as a markdown file in the log folder, named after the world and timestamped. That's the one to use when the person writing the plugin isn't the person with the character: run it on each character and send the files.

Both only speak for what the server has actually sent this session, so play for a bit first — and on 3Scapes, turn the feeds on with `vtoggle`.

**Switching characters without reconnecting works.** MIP is registered per *login*, not per connection, so the handshake sent when you connect doesn't cover a second character who logs in on the same session. Scrye notices the password prompt, clears the previous character's `character.*`, `enemy.*` and `vik.*` state so none of their numbers linger in a HUD, and re-sends the handshake at the next prompt — you'll see `[MIP] new login - handshake will re-send` in the output. If a MUD lets you swap characters *without* re-authenticating, that isn't detectable and you'll need to reconnect.

The server binds to **loopback only**. On the same machine that's `http://127.0.0.1:4747`; to reach it from a phone you need [Tailscale](https://tailscale.com) in front of it, which also gives you HTTPS (iOS won't allow notifications or home‑screen install without it). `.companion tailscale` prints the one command you need; the full walkthrough — including the login and consent steps that aren't obvious — is in **`docs/Scrye-Companion-Setup.md`**.

Once Tailscale is serving, the phone URL looks like `https://desktop-xxxx.your-tailnet.ts.net/` — scan the panel's QR code rather than typing it. If the phone is signed into the same tailnet, Scrye recognises it by its Tailscale login and **you never type the token either**. The token is the fallback for anything off the tailnet, and it changes every time the server starts.

### Putting it on the home screen

**On iPhone / iPad**, do this in **Safari** — Chrome on iOS cannot install web apps, and notifications on iOS only work from an installed app.

1. Open the `https://…ts.net/` URL in Safari.
2. Tap the **Share** button, scroll down, tap **Add to Home Screen**.
3. Launch it from the home‑screen icon. It runs full‑screen with no browser chrome.

**On Android**, installing is optional. Chrome gives an ordinary tab everything the companion needs, notifications included — there's no equivalent of the iOS restriction. If you want the icon and the full‑screen window anyway, open the URL in Chrome and use **⋮ → Add to Home screen** (or the install prompt Chrome offers on its own). Firefox and Samsung Internet work too.

### Using it

Three tabs across the top:

- **Output** — the game's text, in colour, with a command line, a **↑** history button and a command pad underneath.
- **Chat** — your capture panes (Chats, and any others plugins route into), each on its own sub‑tab.
- **Panels** — your HUD panels, rendered from the same specs the desktop uses. Gauges, bars and text update live; buttons, input fields and colorgrid cells are all tappable and fire the same plugin callbacks they do on the desktop. `text` widgets render the plugin colour markup exactly as the desktop does — colours, bold, and `click=` runs as tappable links — so a plugin report's inline actions (dispatch a trade, target a town, pick a route) work by tap. `row` containers lay out side by side too, wrapping onto the next line when the screen is too narrow. Two desktop-only exceptions: colorgrid micro-icons fall back to the character grid, and hover affordances don't exist on a touch screen.

The header shows the connection dot, the character, and vitals. The **⋯** menu switches worlds and enables notifications.

One deliberate restriction: the phone **cannot run script**. Commands starting with `/` (the script console) are rejected from a companion device. Everything else — commands, aliases, sequences, panel buttons — works normally.

### Notifications

Tap **⋯ → Enable notifications** on the phone and accept the permission prompt. Then `.companion notify test` from the desktop to confirm it arrives. On iOS the button only works once Scrye is on the home screen; on Android it works straight away.

What actually fires one:

- **Triggers** with **Notify** ticked. `.companion notify` lists them for the current world, including any that are currently disabled.
- **Plugins** calling `scrye.notify()`. These can't be enumerated (plugin code is arbitrary), so the list mentions them but can't name them.

On 3Scapes, the **3s-chat** plugin is the usual source. It notifies on **tells by default** and on nothing else, so your pocket stays quiet during ordinary channel chatter:

| Command | What it does |
|---|---|
| `chat notify` | Show what currently notifies. |
| `chat notify tells off` \| `on` | Turn tell notifications off or back on. |
| `chat notify <channel>` | Also notify for that channel. |
| `chat unnotify <channel>` | Stop notifying for that channel. |
| `chat watch <name>` | Notify (and beep) when that name appears in any chat message. |
| `chat unwatch <name>` / `chat watched` | Remove one / list them. |
| `chat sound off` \| `on` | Silence just the PC beep — the phone still buzzes and the pane still marks. |

All of these persist per character. The sound toggle exists because the beep and the push
notification serve different moments: sitting at the PC you may want quiet, while the phone
should still buzz when you walk away.

**The Companion panel is the overview.** Its NOTIFICATIONS section lists the notify-flagged
triggers, and a PLUGIN SOURCES section below it shows what each reporting plugin will buzz
the phone about — with a live **on/off button per row** that runs the plugin's own toggle
command, so you flip sources without remembering any syntax. The bundled sources:

| Plugin | Source | Default | Command |
|---|---|---|---|
| 3s-chat | Tells / sound / channels / watched names | tells on | `chat notify …` (table below) |
| 3s-raid | Fleet returns; each auto-dispatch | off | `araid notify fleet\|send on\|off` |
| 3s-chaossea | Bot pauses: goal found, wimpy, out of rooms, idle guard | on | `cs notify on\|off` |
| 3s-stepper | Route done / arrived home / idle guard | on | `.set notify on\|off` |
| 3s-viking-status | Each cart the auto-trader sends | off | `atrade notify on\|off` |

The bot plugins default **on** because their notifies fire exactly when the bot has stopped
and is waiting for you; the raid and auto-trade ones default **off** because they fire during
routine operation.

**If nothing arrives**, debug in this order:

1. `.companion status` — if it says **0 devices registered**, the phone was never subscribed.
   iOS Settings toggles don't create a subscription; tap **Enable notifications** *inside* the
   installed companion app.
2. `.companion notify test` — the readout is a real verdict, e.g. `delivered 1, pruned 0
   expired, 0 failed`. A failure names its cause verbatim from the push service
   (`403 from web.push.apple.com: {"reason":"BadJwtToken"}`), so a broken key, clock skew or
   an oversized payload is a visible sentence instead of a silent shrug.
3. If the test delivers but the game never notifies — nothing is *configured* to notify.
   Set the **Notify** flag on a trigger or use the chat commands above.

## Where files live

Scrye stores its data under your user profile:

| What | Location |
|---|---|
| Profiles | `%APPDATA%/Scrye/profiles` |
| Plugin storage (`scrye.store`) | `%APPDATA%/Scrye/plugin-data/<world>/<plugin-id>.json` |
| Plugin logs (`scrye.log`) | `%APPDATA%/Scrye/logs/plugins/<plugin-id>.log` |
| Crash / session logs | `%APPDATA%/Scrye/logs` |
| User plugins | `%APPDATA%/Scrye/plugins` (also loaded from the `plugins` folder next to the app) |
| Sounds | `%APPDATA%/Scrye/sounds` |
| Companion push signing key | `%APPDATA%/Scrye/companion-vapid.json` (generated once; deleting it un‑registers every phone) |
| Registered phones | `%APPDATA%/Scrye/companion-push.json` |

(On macOS/Linux "%APPDATA%" maps to the platform's application‑data folder.)

---

# Part 2 — Writing plugins

A plugin is a folder with a manifest and a script. When enabled for a character, its script runs once (registering hooks, timers, rules, and panels), then reacts to game events for the life of the session.

## Anatomy

```
my-plugin/
  plugin.json     ← manifest
  main.lua        ← entry script
```

Drop the folder into `%APPDATA%/Scrye/plugins/` (or the `plugins` folder next to the app), then enable it from the Plugins panel and hit **Reload** after edits.

## The manifest — `plugin.json`

```json
{
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "author": "Your Name",
  "description": "What it does, in one line.",
  "mudIds": ["*"],
  "entry": "main.lua",
  "lang": "lua",
  "data": { "areas": "areas.json", "badwords": "badwords.txt" },
  "enabled": true,
  "requires": { "scryeApi": ">=1.1 <2.0" },
  "permissions": ["output.read", "commands.send", "ui.panels"],
  "panes": ["Chats"]
}
```

| Field | Meaning |
|---|---|
| `id` | Unique id. Used for storage, logs, and its state namespace. Keep it stable. |
| `name` | Display name in the Plugins panel. |
| `version`, `author`, `description` | Metadata. `version` is *your* version, unrelated to the API version. |
| `mudIds` | Which worlds it applies to. `["*"]` (or empty) = all worlds; otherwise a list of MUD ids. |
| `entry` | Entry script relative to the folder. Default `main.lua`. |
| `lang` | `"lua"` (native Lua 5.4), `"js"` (Jint), or `"wasm"` (compiled WebAssembly — see below). Default `lua`. |
| `data` | Data files the plugin ships, as script key → file name. See below. Optional. |
| `enabled` | Whether it's a candidate to load. Users still opt in per character. |
| `requires` | Compatibility constraints. See below. Optional. |
| `permissions` | What the plugin intends to do, shown to the user. See below. Optional. |
| `panes` | Capture panes the plugin writes to. The host creates them when it loads. See below. Optional. |

## Declaring capture panes — `panes` (API 1.11)

A capture pane normally appears the first time something is routed into it. That is fine once
you've used the plugin for a while — the pane is in your saved layout and comes back on every
start — but it reads as a bug the first time: enable the chat plugin on a new machine, see
nothing, and wonder whether it worked. The pane only turns up when somebody eventually speaks.

List the panes your plugin captures into and the host creates them at load instead:

```json
"panes": ["Chats"]
```

Names are matched to `scrye.capture` case-insensitively, so declare them exactly as you use them.
The field is optional and additive — an older client ignores it, so you can add it without raising
your `requires` range.

Two things it deliberately does **not** do. It won't resurrect a pane you closed by hand: that
choice is remembered per world and survives restarts, and only a line actually routed into the
pane brings it back (at which point there's something in it to read). And disabling the plugin
leaves the pane alone rather than deleting it along with whatever it had captured.

## Shipping data with a plugin — `data`

A plugin has no filesystem. What it has instead is a `data` map in the manifest: name the files
your plugin ships, and the host reads them from your plugin folder at load and hands them over as
`scrye.data.<key>`. A word list, a route table, a room map, a colour palette — anything that is
your plugin's *source* rather than its state belongs here, instead of being pasted into the script
as a giant literal.

```json
"data": { "areas": "areas.json", "badwords": "badwords.txt" }
```

```lua
local AREAS = scrye.data and scrye.data.areas
for _, word in ipairs(scrye.data.badwords or {}) do ... end
```

**The file extension picks the shape:**

| Extension | You get |
|---|---|
| `.json` | A nested table — objects become tables, arrays become 1-based lists, numbers are Lua numbers. |
| `.txt` `.list` `.lines` `.words` | A list of the non-blank lines, trimmed, with `#` comment lines dropped. |
| anything else | The raw text, for a format your plugin parses itself. |

JSON rather than Lua for structured data: the sandbox has no `load`/`dofile`, so a `.lua` data
file could only ever reach you as a string.

**Rules.** Plain file names only — no folders, no `..`, no absolute paths; the file must sit
directly in your plugin folder. Files are read-only and capped at 4 MB each, 32 entries per
manifest. A key must be a valid identifier so `scrye.data.areas` works with dot access.

**Failure is per-entry.** A missing file, malformed JSON or an oversized file drops *that key*,
prints why to the world output, and lets the plugin load anyway. So check before you use it —
`scrye.data.areas` being nil is the case where the author (you) shipped a broken file, and saying
so beats behaving as if the data were empty.

This is not storage. Nothing writes here; `scrye.store` is still where state between sessions
goes.

## The idle guard (dead-man's switch)

Automation that runs while nobody is watching is the thing most likely to get you in trouble —
a bot that keeps walking an area for six hours after you fell asleep, a heal timer firing into an
empty room. Scrye watches for that and stops.

```
.idle              show the current setting
.idle on / off     turn it on or off for this session
.idle 600          limit in seconds
.idle 10m          the same, in minutes
```

Or set it in a profile layer, where it inherits Global → MUD → Account → Character like everything
else:

```json
{ "idleGuard": true, "idleGuardSeconds": 600 }
```

**What counts as you being here** is anything *you* send: typed commands, macro keys, and clicks on
a plugin's panel links — they all arrive through the same path. What deliberately does **not** count
is output from the MUD, or anything a trigger, timer or plugin sends. A bot producing output all
night must never look like someone at the keyboard; that is the whole point.

**What happens.** At 80% of the limit you get one warning — type anything to reset it. At the limit,
Scrye suspends its own profile timers and pauses a running sequence, and fires `scrye.onIdle` in
every plugin so each one stops what it is driving. Your next command resumes the timers and the
sequence automatically, because the hazard was being away and you are back. Plugins stay stopped
until you restart them deliberately — `..` for the stepper, `cs auto on` for the chaos sea —
since a bot silently resuming because you typed `look` is exactly the surprise this feature exists
to prevent.

Off by default, limit 600s, clamped to 60–7200s.

**In a plugin:**

```lua
scrye.onIdle(function()
  if running then stop() ; scrye.print("idle guard fired - stopped") end
end)
```

## The plugin API version

Scrye versions the plugin contract — the `scrye.*` functions, the widget vocabulary, the manifest
schema — **separately from the application**. Scrye 2.4 and Scrye 3.1 might both speak plugin API
1.1. Declare what you need:

```json
"requires": { "scryeApi": ">=1.1 <2.0" }
```

A client that can't satisfy the range refuses the plugin at load and says so in the world output
and the Plugins panel, rather than letting your script die on a missing function forty lines in.

**The grammar** is space-separated constraints, all of which must hold. Each is an operator
(`>=`, `>`, `<=`, `<`, `=`) plus a version, or a bare version meaning `>=`. Versions are `major`
or `major.minor`.

| Spec | Means |
|---|---|
| `">=1.1 <2.0"` | The usual shape: needs a 1.1 feature, expects 2.0 to break it. |
| `"1.1"` | 1.1 or newer, including 2.x. Fine if you only use long-stable calls. |
| `"=1.1"` | Exactly 1.1. Rarely what you want. |

**Semantics.** A **minor** bump only adds — new functions, new widget types, new optional manifest
fields — so everything that worked keeps working. A **major** bump removes or changes meaning, and
plugins written for the previous major are expected to break.

Omitting `requires` entirely means "load me anywhere", which is what every plugin written before
this field existed does. That's fine for simple plugins; declare a range once you depend on
something specific.

**Current API version: 1.12.** Recent history: 1.2 added inline colour markup in `scrye.print`/
`scrye.capture`; 1.3 markup in `text` widgets, colorgrid `labels`, and bound buttonrows; 1.4 the
manifest `data` map (`scrye.data.<key>`); 1.5 `scrye.onIdle`. **1.6 is the automapper batch**, all
additive: `scrye.onCommand` (observe every outgoing command), `scrye.json` (encode/decode),
`scrye.store.setMany` (N keys, one disk write), `scrye.emit`/`scrye.on` (inter-plugin events),
colorgrid `onHover`, and sub-second timers (250 ms resolution). **1.7 adds colorgrid
`weave = true`**: even cells render as full tiles and odd cells as thin connector lines
(`-` `|` `/` `\` `x` in their palette colour), so a map can draw rooms on even cells and the
exits between them on the odd cells they share; click/hover coordinates stay raw (halve the
even ones), and the companion simply shows the same characters as an ASCII map. **1.8 adds
colorgrid `icons = { ["char"] = "glyph" }`** — micro-icons: an iconed cell draws a muted tile
of its palette colour with a tiny host-drawn vector glyph on top, so terrain reads as terrain.
The glyph vocabulary: `water dashes grass hill tree pine mountain house tower gate ruin star
person ship anchor flag bolt crown hammer cross dot`. Icons beat `labels` letters for the same
character, cells under 8 px fall back to plain tiles (then the letter rules), unknown names
render as plain tiles, and the companion ignores the map — the character grid remains the
text fallback. 1.8 also adds colorgrid `cell = N`: the cell-size ceiling, default 12 px
(clamped 3–64) — raise it when a chart's icons deserve room (the viking sea chart uses 24);
cells still shrink to fit the panel width. And the **`row` container**
(`{ type = "row", widgets = { ... } }`): its children are ordinary widgets laid out side by
side, each at its measured width — the escape hatch from the panel's vertical stack. The
viking sea chart uses it to put the resolve choices beside the chart instead of below it.
The companion renders rows side by side too, wrapping children onto the next line when the
screen is too narrow for them. **1.9 adds `onRightClick`** as a second, distinct action on
`colorgrid`, `button` and `buttonrow` (the companion maps a long-press onto it); **1.10 adds
`scrye.onRelay`**, chat arriving from another open world; **1.11 adds the manifest `panes`
field**, so a plugin's capture panes exist as soon as it loads instead of on first traffic;
**1.12 adds the `barlist` stages field**, a seventh column of `qty,pct;…` rawest first, so a
bar draws one segment per quality stage instead of a single amber/green split.

## Permissions

```json
"permissions": ["output.read", "commands.send", "ui.panels"]
```

These are shown to the user in the Plugins panel before they enable your plugin. Declaring them is
good manners and will matter more later.

**Be clear about what this is.** Today these are *declarations, not enforcement* — nothing stops a
plugin that didn't declare `commands.send` from calling `scrye.send`. The real sandbox is the
scripting engine: no `io`, no `os.execute`, no filesystem, no CLR access, and that part is
enforced. What is **not** bounded is the thing that matters most on a MUD — `scrye.send` can issue
any command your character can type. A plugin can't read your files; it can drop your inventory.
That's why `commands.send`, `output.modify`, `aliases.manage` and `variables.write` are flagged as
sensitive in the manager.

| Permission | What it means |
|---|---|
| `commands.send` | Send commands to the MUD as you |
| `output.modify` | Hide or rewrite lines before you see them |
| `output.read` | Read everything the MUD sends |
| `triggers.manage` / `aliases.manage` | Add its own triggers / aliases |
| `variables.read` / `variables.write` | Read / change world variables shared with your triggers |
| `state.read` / `state.write` | Read character state / publish into the state tree |
| `timers.manage` | Run code on a timer |
| `storage.private` | Save data between sessions |
| `notifications.show` | Show notifications (and push to your phone) |
| `sound.play` | Play sounds |
| `capture.write` | Route lines into capture panes |
| `log.write` | Write to its own log file |
| `ui.panels` | Add HUD panels |

Unknown names are shown verbatim rather than hidden, so a plugin written for a newer Scrye still
communicates its intent on an older one.

## The scripting environment

Lua plugins run on **native Lua 5.4** (the reference implementation, via KeraLua) in a **sandbox**: you get the standard library — `string`, `table`, `math`, `utf8`, `coroutine`, `os.time`/`os.date`/`os.clock`, `pcall`, `tonumber`, etc. — but **no `io`, no `os.execute`, no filesystem, no `require`, no `load`**. 5.3/5.4 language features are available: integer division `//`, bitwise operators, `goto`, and the integer subtype. For durable data use `scrye.store` (key/value) and `scrye.log` (a log file); the host handles the file I/O for you.

JavaScript plugins run on **Jint** and see the same `scrye.*` API (with JS idioms: arrays instead of Lua tables, `onSubmit`/`onClick` as functions, etc.).

WebAssembly plugins (`"lang": "wasm"`) are **compiled modules** speaking the
[scrye-wasm-abi](scrye-wasm-abi.md). Choose wasm when a plugin outgrows scripting:
pathfinding over big graphs, heavy text crunching, or when you want to ship a binary.
Three things are different about wasm plugins:

- **Permissions are enforced**, not just declared: an API call whose permission isn't in
  your manifest traps with a message naming it. (Lua/JS permissions remain declarations.)
- **Runaway protection is real**: every callback runs under a ~100 ms deadline and a
  64 MB memory cap. An infinite loop traps and counts toward quarantine instead of
  freezing the client.
- **No ambient anything**: no clocks, no filesystem, no randomness — only the `scrye`
  API surface exists.

The supported authoring path is Rust, via the `scrye-plugin` SDK in `sdk/rust/`
(closure-based API that feels like the Lua one — see `sdk/rust/examples/hp-watch`, and
`sdk/rust/plugins/3s-pathfinder` for the real thing: BFS path search that 3s-map
delegates its `map goto` to over inter-plugin events, with automatic fallback to the
Lua search when the pathfinder isn't loaded):

```
rustup target add wasm32-unknown-unknown
cargo build --release --target wasm32-unknown-unknown
```

Copy the built `.wasm` next to your `plugin.json` and set `entry` to it. Any language
that emits a core wasm module can target the ABI; the spec document is authoritative.

Everything runs on the session's loop thread, so your script never re‑enters concurrently — you don't need locks.

## The `scrye.*` API

### Output & input
| Call | Description |
|---|---|
| `scrye.print(text)` | Echo a line to the local output, tagged with your plugin id. |
| `scrye.send(text)` | Send a command to the MUD. |
| `scrye.log(text)` | Append a line to your plugin's log file. |

### State & variables
The **state tree** is a shared key/value space the whole app reads from. Game feeds publish into it (e.g. `character.health.current`, `enemy.name`, and MUD‑specific feeds like `vik.*`). Plugins publish their own derived state under `plugin.<id>.*` and bind HUD widgets to it.

| Call | Description |
|---|---|
| `scrye.getState(path)` | Read a state value as a string (`""` if unset). |
| `scrye.setState(path, value)` | Publish a value (use `"plugin." .. scrye.id .. ".key"`). |
| `scrye.watch(path, function(value, path) ... end)` | React whenever a state path (or subtree) changes. |
| `scrye.getVariable(name)` / `scrye.setVariable(name, value)` | Read/write a world variable (shared across plugins & triggers). |

### Event hooks
| Call | Fires when |
|---|---|
| `scrye.onLine(function(line) ... end)` | Each output line. **Return `false` to gag** the line, or **return a string to rewrite** it. |
| `scrye.onPrompt(function() ... end)` | The MUD shows a prompt. |
| `scrye.onIdle(function() ... end)` | The idle guard fired — nobody is at the keyboard. Stop whatever you are driving. |
| `scrye.onConnect(fn)` / `scrye.onDisconnect(fn)` | Connection opens / closes. |
| `scrye.onGmcp(function(json, package) ... end)` or `scrye.onGmcp("Char.Vitals", fn)` | A GMCP message (optionally filtered by package). |
| `scrye.onChannel(function(channel, message) ... end)` or `scrye.onChannel("Party", fn)` | A MIP chat message. Tells arrive with channel `"Tell"`. |
| `scrye.onRelay(function(world, channel, message) ... end)` or `scrye.onRelay("Tell", fn)` *(1.10)* | Chat from **another open world**, delivered here because this world's tab is in front. Same filtering as `onChannel`, with the source world as an extra first argument. |
| `scrye.onCommand(function(command) ... end)` | *(1.6)* A command went out to the MUD — **any** origin: typed input (after aliases), macros, sequences, triggers, other plugins. Observe-only; the send already happened, nothing you return changes it. **Never `scrye.send` from inside the handler** — that send re-fires every onCommand hook, including yours. This is the hook that makes an automapper possible: it sees moves it didn't originate. |

### Timers
| Call | Description |
|---|---|
| `scrye.after(seconds, fn)` → id | Run once after a delay. |
| `scrye.every(seconds, fn)` → id | Run repeatedly. |
| `scrye.cancel(id)` | Cancel a timer. |

Since API 1.6, `seconds` honours fractions: the scheduler ticks at **250 ms**, so that is the
effective resolution and floor — `scrye.after(0.25, fn)` works, `scrye.after(0.01, fn)` fires on
the next tick anyway. Profile timers (the ones in Settings) still tick in whole seconds.

### Inter-plugin events *(1.6)*

| Call | Description |
|---|---|
| `scrye.emit(name, data)` | Broadcast an event to **every** loaded plugin — including yourself. `data` is a string; use `scrye.json` for structured payloads. |
| `scrye.on(name, function(data, name, source) ... end)` | Handle an event. `name` matches case-insensitively; `source` is the emitting plugin's id. |

This replaces the world-variable side-channels plugins used to coordinate through (`party`,
`cs_auto`, …). Emit chains are capped at depth 8 — an A-emits→B-emits→A cycle is cut with a
report, not a hang. An emit from your load script (before the session finishes wiring) is
dropped: register handlers at load, emit from hooks.

Conventions the bundled plugins speak over this channel: `map.path.find` / `map.path.result`
(BFS delegation to the wasm pathfinder — see `sdk/rust/plugins/3s-pathfinder`), `map.room` /
`map.walk.started` / `map.walk.stopped` (the automapper's position feed), and **`map.hold`**
(`{"on":true|false}`): suspend the automapper while YOUR plugin owns movement through space
that must not be mapped. The chaos-sea explorer holds the map while a randomly generated sea
is active, so sea steps never dead-reckon phantom rooms into a real area. The hold is
transient — never persisted, released by the sender, shown on the map panel as
`HELD (<plugin>)`, and `map on` always overrides it.

### JSON *(1.6)*

| Call | Description |
|---|---|
| `scrye.json.encode(value)` → `json` or `nil, err` | Any nil/boolean/number/string/table to JSON text. |
| `scrye.json.decode(json)` → `value` or `nil, err` | JSON text back to Lua values, same shapes as `scrye.data` files. |

Shape rules worth knowing: a table whose keys are exactly `1..n` encodes as an array, anything
else as an object; an **empty table encodes as `{}`**; integral numbers encode without a decimal
point (`42`, not `42.0`). Functions aren't data — encoding one gets you `nil, err`, as does
decoding malformed text; check the error instead of trusting the input. This is the intended way
to keep structured state in `scrye.store` — stop hand-rolling `z|x|y|...` line formats unless you
really want them.

### Rules
| Call | Description |
|---|---|
| `scrye.addTrigger{ pattern=, regex=, ignoreCase=, send=, run= }` | Match output; `send` a command and/or `run` a function. |
| `scrye.addAlias{ ... }` | Match what the user types; a match consumes the input. |

`pattern` is required. `regex=true` for regex (else plain substring). `ignoreCase` defaults to true. `run` receives regex capture groups as arguments.

### Persistent storage
Survives restarts (`%APPDATA%/Scrye/plugin-data/<world>/<id>.json`), scoped to your plugin:

```lua
scrye.store.set("key", "value")
local v = scrye.store.get("key")   -- string or nil
scrye.store.delete("key")
local keys = scrye.store.keys()    -- { "k1", "k2", ... }
scrye.store.setMany{ a = "1", b = "2" }   -- (1.6) N keys, ONE disk write
```

Values are strings — serialize tables with `scrye.json.encode` (1.6). Every `set()` rewrites the
plugin's whole store file, which is fine for a counter and quadratic for a mapper flushing forty
area keys; that is what `setMany` is for. Unchanged values in a batch are skipped, and a batch
that changes nothing writes nothing.

### Alerts & routing
| Call | Description |
|---|---|
| `scrye.notify(text)` | Toast notification (+ taskbar flash when unfocused; pushed to registered phones). |
| `scrye.sound("beep")` | Play `"beep"`, a path, or a file in the sounds folder. |
| `scrye.capture(pane, text)` | Route a line into a named capture pane. Declare the pane in the manifest's `panes` *(1.11)* so it exists from load rather than from the first line. |

**Reporting notification sources — the `plugin.<id>.notify` convention.** A plugin that
calls `scrye.notify()` should also *say so*, so the Companion panel can show the user what
will buzz their phone and let them toggle it. This is plain state, not an API call: publish
newline-joined rows to exactly `plugin.<id>.notify`, one source per row, four tab-separated
fields:

```
label \t detail \t state \t command
```

The `state` field decides what the panel draws, and there are four kinds:

| `state` | Renders as | `command` is |
|---|---|---|
| `on` / `off` | a live toggle button | the command that flips it |
| `add` | a text box + **Add** button | a **template** containing `{}`, replaced by what the user types |
| `item` | one list entry with a **✕** | the command that *removes* that entry |
| anything else | plain informational text | ignored |

Together `add` and `item` make an editable list. The chat plugin reports its watched names
that way — one `add` row (`chat watch {}`) plus an `item` row per name (`chat unwatch Goran`)
— so the whole thing is editable from the panel without typing a command.

Every command runs through the normal input pipeline, so **your own alias handles it** and
the panel never learns what a channel or a watched name is. One trap worth knowing: an
`item`'s command must genuinely *remove*. If your add command is a setter rather than a
toggle, reusing it for the ✕ gives you a button that silently does nothing.

Re-publish the rows from every place the settings change and once at load, and persist the
flag in `scrye.store` — whether a phone buzzes is a preference about the player, not about
one session. Needs `state.write` (and `notifications.show` for the notifying itself). The
convention is voluntary, which is why the panel still admits that non-reporting plugins may
notify on their own.

### HUD panels
`scrye.addPanel{ ... }` contributes a declarative panel. The host renders it and keeps bound widgets in sync with state.

```lua
scrye.addPanel{
  title = "My Panel",
  width = 300,
  accent = "#5A93D4",     -- title + border color (optional)
  background = "#101418", -- panel fill (optional)
  color = "#D6DEE8",      -- default text color for widgets (optional)
  widgets = { ... },      -- OR tabs = { { title=, widgets={...} }, ... }
}
```

Content is either a flat `widgets` list **or** a set of `tabs`.

## HUD widget reference

Each widget is a table with a `type`. Common fields: `text` (a label/prefix), `bind` (a state path to display), `color` (a `#RRGGBB` override).

| `type` | What it shows | Key fields |
|---|---|---|
| `label` | Static (or bound) text. | `text`, or `bind`; `color` |
| `value` | A prefix plus a live value. | `text` (prefix), `bind`; `color` |
| `text` | A multi‑line monospaced block (reports/tables). | `bind`; `color` |
| `gauge` | A labeled bar; auto‑colors cyan→amber→red by ratio unless `color` set. | `text`, `value`, `max` (state paths or numbers); `color`; `dim = true` (darken toward black as the value falls, using `color` as the base hue — green when unset — instead of the ratio ramp) |
| `progress` | A labeled bar with an explicit color. | `text`, `value`, `max`; `color` |
| `button` | A clickable button. | `text`, `action = function() ... end`, `onRightClick = function() ... end` *(1.9)* |
| `buttonrow` | Several buttons side by side (equal width). | `buttons = { {text=, action=, onRightClick=}, ... }` |
| `input` | An inline text field; **Enter** or the **Set** button submits. | `text` (label), `bind` (seed value), `onSubmit = function(text) ... end` |
| `colorgrid` | A clickable grid of characters, colored by a palette. | `bind` (grid string), `palette = { ["#"]="#RRGGBB", ... }`, `onClick = function(col, row, ch) ... end`, `onHover = function(col, row, ch) ... end` *(1.6)*, `onRightClick = function(col, row, ch) ... end` *(1.9)*, `weave = true` *(1.7 — even cells are tiles, odd cells draw `-` `\|` `/` `\` `x` as thin connector lines)*, `icons = { ["char"] = "glyph", ... }` *(1.8 — micro-icons; see the glyph vocabulary above)*, `cell = N` *(1.8 — cell-size ceiling in px, default 12, clamped 3–64)* |
| `list` | A dynamic list of rows: `label`, or `label \t value` with the value right‑aligned and dimmed. Grows and shrinks with the bound value. | `bind`; `separator` (default tab); `color` |
| `table` | The same rows split into columns, with optional headers and per‑column alignment. | `bind`, `columns = {...}`, `align = "llr"`, `separator`; `color` |
| `barlist` | A list of labelled bars from one bound value — a compact way to show several quantities against a common maximum. Rows are `label \t caption \t value \t max \t refined [\t tooltip [\t stages]]`; anything else in the bound value draws as plain text, so headers still work. | `bind`; `color` |
| `row` | A horizontal container *(1.8)*: its children lay out side by side at their measured widths, the escape hatch from the panel's vertical stack. | `widgets = { ... }` (ordinary widget tables) |

Notes:

- **Dynamic content** flows through `bind` + `setState`: the *set* of widgets is fixed when the panel is built, but their bound content updates live. `list` and `table` are single widgets whose **row count follows the bound value**, so a variable‑length collection needs neither a `text` blob nor a `colorgrid`.
- **To change the widgets themselves, call `scrye.addPanel` again with the same `title`.** The host replaces that panel in place rather than adding a second one: the canvas position and any drag survive, the companion replaces it by the same id, and the old panel's state watches and button callbacks are retired for you. This is the escape hatch for things a plugin cannot know at load — a gauge's *label* is fixed at build time, so a HUD whose stats are named differently per character (per guild, per class) rebuilds once when it finds out rather than trying to relabel in place. Rebuilds are cheap but not free: do it when the *shape* changes, not on every update.
- **If you're reaching for `string.format` and padding, use `table`.** Composing aligned columns in Lua is what `text` widgets forced; the host measures columns for you, and the mobile companion renders a real table rather than pre‑padded text that wraps badly on a phone.
- **Inline markup in `text` widgets renders on both hosts** — colours, `bold`/`underline`/`italic`, and `click=`/`prompt=` runs are tappable links on the companion, dispatched through the same input pipeline as the desktop (plugin aliases get first refusal). The one exception: the `inverse` flag is desktop‑only — the companion ignores it rather than guessing at base colours it only inherits.
- `value`/`max` on gauges/progress accept a **state path** or a **literal number** (e.g. `max = 100`).
- `colorgrid` `onClick` gives you the clicked cell's `col`, `row`, and character — map that back to your data.
- `colorgrid` `onHover` *(1.6)* fires when the pointer moves onto a **different** cell (never per pixel), and once with `(-1, -1, "")` when it leaves the grid so you can clear whatever you were previewing. Hover is desktop-only — the companion's touch screen never fires it — so use it to *enrich* (a room-name readout beside the map), never for anything `onClick` can't also reach.
- **`onRightClick` *(1.9)* is a second action, not a variant of the first.** A right-click runs it and `onClick` does *not* also fire; a left click runs `onClick` and never this. It works on `colorgrid` (same `col, row, ch` as `onClick`), `button` and `buttonrow` (no arguments). Unlike `onHover` it may gate real behaviour, because the companion maps a **long-press (~500 ms)** onto it — so anything reachable by right-click on the desktop is reachable by touch on the phone. Use it for the secondary thing a cell or button obviously affords: left-click a map square to travel there, right-click to inspect it.
- **Behaviour change in 1.9:** colorgrid cell clicks used to fire `onClick` on *any* pointer button, so a right-click silently ran it. Left click now runs `onClick` and nothing else. If a plugin relied on that, it was relying on an accident.
- **`barlist` bars can show a quality breakdown, not just a fill.** Past `max`, the `refined` field splits the fill into raw (amber, left) and refined (green, right). The optional seventh field takes that further: `qty,pct;qty,pct;…` **rawest first** draws one segment per stage — width is how many units sit at that quality, colour is where that quality falls on the amber→green ramp — so the intermediate stages are visible instead of averaged into one split. A row without it renders exactly as before. The sixth field is a hover tooltip (`\n` becomes a line break); the phone can't fire hover, so don't put anything essential only there.
- `input` seeds its field from `bind`; refresh it by `setState`‑ing that key.
- `table` sizes each column to its widest cell. `align` takes one character per column — `l`, `r`, `c` — and right‑aligning numeric columns is worth doing; without it a table of quantities reads worse than the blob it replaced.
- Rows with too few cells render blank in the missing columns, so a "nothing here" row can just be one cell.

### Example — a `table` widget

```lua
local P = "plugin." .. scrye.id .. "."

scrye.setState(P .. "cargo", table.concat({
  "Iron\t120\t18g",
  "Timber\t80\t7g",
  "Silk\t12\t240g",
}, "\n"))

scrye.addPanel{
  title = "Cargo",
  widgets = {
    { type = "table", bind = P .. "cargo",
      columns = { "Good", "Qty", "Price" }, align = "lrr" },
  },
}
```

## Colours and theme tokens

Everywhere a colour is accepted — a widget's `color`, a panel's `accent` / `background` / `color`,
and `colorgrid` palette values — you can pass either a `#RRGGBB` literal **or a semantic token**:

```lua
{ type = "label", text = "Low fuel", color = "warning" }
```

| Token | Meaning |
|---|---|
| `accent` | The scheme's accent — headings, emphasis |
| `text` | Primary body text |
| `dim` | Secondary text, captions, units |
| `bg`, `panel`, `panelalt`, `inset` | Surfaces, increasingly recessed |
| `line` | Borders and separators |
| `success` | Good / healthy / complete |
| `warning` | Caution — worth a look, not broken |
| `error` | Bad / failed / critical |
| `info` | Neutral informational highlight |

**Prefer tokens over literals.** A literal like `"#202020"` hard‑codes one colour scheme into a
client that ships eight including a light one, and the mobile companion has its own palette — the
same token resolves correctly in all of them, a literal doesn't. Tokens are part of the API and
won't be renamed without a major version bump.

Tokens follow the scheme **live**: switching colour scheme re‑resolves every token‑derived
brush in place — panel accents, widget colours, colorgrid palettes, and the `@{...}` markup
inside text widgets all repaint immediately, no reload needed. (Brushes are still immutable
under the hood — the theme change swaps in freshly resolved ones on the UI thread.) Hex
literals are, of course, unaffected — which is one more reason to prefer tokens.

An unrecognised colour name falls back to the theme default rather than rendering something
arbitrary — so a typo shows as "unstyled", not as an invisible widget.

## State namespaces you'll see

- `plugin.<id>.*` — your own published state (bind widgets here).
- `character.*` — vitals, mirrored from MIP (or GMCP) so a HUD binds to one spelling whatever the source: `character.health.current`/`.max`, `character.spell.current`/`.max`, `character.gold.a`/`.amax` and `character.gold.b`/`.bmax` (the two guild‑point slots), plus `character.gline1`/`.gline2`, the guild's own status lines.
- `character.gline1.raw` / `character.gline2.raw` — the same lines **before** their colour tags are stripped. Display the plain ones; parse the raw ones. See below.
- `enemy.name`, `enemy.health` — current target.
- Game‑specific feeds — on 3Scapes every guild's MIP key/value feed lands under `vik.*` (`vik.daler`, `vik.wstock`, `vik.carts`, `vik.buildings`, …), readable by any plugin via `scrye.getState("vik.<key>")` and watchable with `scrye.watch("vik", fn)`. The prefix is historical — the Viking guild was first to use the carrier, not the only one.
- `mip.<tag>` — the raw payload of a MIP frame type Scrye has no decoder for, so a plugin can use a feed the client hasn't learned yet. Run `.mip fields` to see which tags a character receives.

## When a plugin misbehaves

Scrye watches what plugins cost and whether they work, because everything a plugin does on an
output line runs **synchronously on the session loop** — a slow plugin is not slow in isolation,
it delays that world's output.

- **Slow callbacks.** A single call over 50 ms is reported in the world output with the plugin's
  name and its running average, then rate‑limited to one message per plugin per 30 s so a slow
  plugin can't flood the output it's already slowing down. The Plugins panel shows a running
  count and the worst case.
- **Repeated failures.** Errors in a callback are caught and printed (one bad plugin never takes
  down line processing), but they're also counted. **Ten consecutive failures unloads the
  plugin** and says why. Consecutive, not total — a plugin that throws on one odd line a day
  isn't broken; one that throws on every line is. Any success resets the streak.
- **Getting it back.** Quarantine isn't persisted, so reconnecting clears it. Pressing **Reload**
  wipes the plugin's error history and gives it a clean run — that's the button to press after
  you've fixed the script.

If the Plugins panel shows a plugin that simply won't turn on, check the row: an API‑range
mismatch is reported there explicitly rather than looking like a plugin that quietly does nothing.

## Installing, reloading, packaging

- **Install:** drop the plugin folder into `%APPDATA%/Scrye/plugins/`, enable it for a character in the Plugins panel.
- **Reload:** after editing the script, click **Reload** — it re‑reads from disk live.
- **Share:** zip the plugin folder and name it `<something>.scryeplugin` — the `plugin.json` must be at the archive root or inside a single top-level folder. The recipient drops the file straight into their plugins folder and presses **↻**: Scrye extracts it to `<plugins>/<id>/` and deletes the archive. A plain `.zip` still works if they unzip it themselves.

## Lua gotchas (worth knowing)

Scrye runs real Lua 5.4 (it used the managed MoonSharp engine before; that engine's quirks — stale uninitialized locals, "pattern too complex" aborts — are gone). What's left is ordinary Lua-and-Scrye knowledge:

- **Integers are real (5.4).** `math.floor` returns an integer, `7 // 2` is `3`, and `string.format("%d", x)` **errors** if `x` has a fractional part — `math.floor` it first (this was silently truncated on the old engine).
- **Runaway loops abort.** Every callback runs under an instruction budget (~200M VM instructions, a few hundred ms). An accidental `while true do end` raises "exceeded its execution budget" instead of freezing the client, and repeated offenders quarantine like any erroring plugin.
- **Timers tick at 250 ms** (since API 1.6). Fractional seconds work down to 0.25; don't rely on anything finer.
- **Colors are `#RRGGBB` strings.** The host builds thread‑safe brushes for you; just pass hex.
- **State is strings.** Numbers come back as text — `tonumber(...)` them.

## A complete example

A tiny plugin that watches your HP, warns when it's low, adds an `hp` command, and shows a gauge:

`plugin.json`
```json
{
  "id": "hp-watch",
  "name": "HP Watch",
  "version": "1.0.0",
  "author": "You",
  "description": "Low-HP warning + a gauge",
  "mudIds": ["*"],
  "entry": "main.lua",
  "lang": "lua",
  "enabled": true
}
```

`main.lua`
```lua
local P = "plugin." .. scrye.id .. "."

-- warn once when HP drops below 30%
local warned = false
scrye.watch("character.health.current", function()
  local cur = tonumber(scrye.getState("character.health.current")) or 0
  local max = tonumber(scrye.getState("character.health.max")) or 1
  local pct = max > 0 and (cur * 100 / max) or 100
  scrye.setState(P .. "hpline", string.format("HP %d/%d (%d%%)", cur, max, math.floor(pct)))
  if pct < 30 and not warned then
    warned = true
    scrye.notify("Low HP!")
    scrye.sound("beep")
  elseif pct >= 30 then
    warned = false
  end
end)

-- an alias: typing "hp" prints the current line
scrye.addAlias{
  pattern = "^hp$", regex = true,
  run = function() scrye.print(scrye.getState(P .. "hpline")) end,
}

-- a small HUD panel with a live gauge and the text line
scrye.addPanel{
  title = "HP Watch",
  width = 240,
  accent = "#D6524E",
  widgets = {
    { type = "gauge", text = "HP",
      value = "character.health.current", max = "character.health.max" },
    { type = "value", text = "", bind = P .. "hpline", color = "#E0A830" },
  },
}
```

Reload it, and you get a gauge, an `hp` command, and a low‑HP warning — a good template to build from.

---

*Scrye is a clean‑room successor to MUSHclient. If you're porting a MUSHclient plugin: miniwindows become `scrye.addPanel`, `SetVariable`/`GetVariable` config becomes `scrye.store`, `DoAfterSpecial` becomes `scrye.after`, `Send`/`Note` become `scrye.send`/`scrye.print`, and inputboxes become `input` widgets or `atrade`-style commands.*
