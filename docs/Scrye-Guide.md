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
  - **Up / Down** — walk back and forth through the whole command history. The same command
    never comes up twice, however many times you ran it.
  - **Ctrl+Up / Alt+Up** (and Down) — the same walk, **filtered by what you have already
    typed**: type `vtrade `, hold Ctrl or Alt, and press Up, and you cycle only the vtrade
    commands. The filter is the text *before the caret*, so you can park mid-line and filter
    on a stem, and it is fixed when the walk starts — editing the box starts a fresh one.
    Alt+Up is MUSHclient's key for this; Ctrl+Up does the same thing.
  - **Tab** — complete the word under the caret from words seen in the output.
  - **PgUp / PgDown** — page through the scrollback. They work from anywhere in the window
    — the command line, the output pane, a HUD panel — so you never have to click into the
    output first. Inside a capture pane they page *that* pane, which keeps its own place.
    The exceptions are controls with pages of their own (a multi-line box, a list, the world
    tree) and the Settings/Edit-world overlays, which keep the keys while they are open.
    One line of overlap between pages; paging back to the bottom resumes following the
    newest lines.
  - **Ctrl+F** — open the find bar to search the scrollback.
  - **Esc** — clear the input.
  - Global Settings → Appearance has **Keep the last command in the input box**: after Enter
    the command stays put with its text selected, so Enter on its own repeats it and typing
    replaces it. Off by default — the box clears, as it always has.
- **Click almost anywhere and start typing.** The command line takes focus back after a click
  that had nothing better to do with it — the output, a HUD panel, a world tab. Controls that
  use the keyboard keep it: text boxes, buttons, the world list, scrollbars, and the output
  pane while a selection is up, so Ctrl+C still copies what you just dragged over. The
  **▼ back to bottom** chip is the exception among buttons — it hands the keyboard back,
  because "take me back to live" is the moment you want to be typing.
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
owns it. Neither does anything a trigger, timer or plugin sends to the **World** — the
separator is for what a person types, and a plugin sending `say a;b` means one command. A rule
whose destination is **Client** is the one exception, and it splits only the template you
typed into the Send box, never text a wildcard carried in from the MUD (see *Reaching a plugin
from a rule*). Clickable links the *MUD* sends (MXP `<SEND>`) are taken literally, for the same
reason they never reach the script console: one click should be one command.

## Reaching a plugin from a rule

A trigger, alias or timer's **Send to** box picks where its text goes, and the choice that
matters here is **Client**.

**World** is the old behaviour: straight to the MUD, alias pipeline skipped. That is right for
`flee` and wrong for `cs pause`, because `cs pause` is a command the *chaos-sea plugin* answers
and 3Scapes has never heard of — sent to the world it comes back "Huh?".

**Client** runs the text the way a line you typed is run: plugin aliases first, then your own
aliases, then the MUD if nothing claimed it. So the whole pause-do-things-resume shape becomes
one rule:

```
Send to:  Client
Send:     cs pause;open cask;get all;cs resume
```

The semicolon splits it the same way it splits a typed line, and one command per line works too
— mix them freely. The split is applied to **what you wrote**, before `%1` and `${var}` are
filled in, so a semicolon that arrives inside a wildcard is part of the text and not a new
command. That matters on a trigger: the MUD authored the line your wildcards captured, and the
MUD does not get to turn one rule of yours into three commands.

Two things a Client send deliberately does *not* do. It does not touch the input box or the
command history — the box is yours. And it does not poke the **idle guard**: a rule firing is
not a person at the keyboard, which is the whole reason the guard can tell a bot from you.

If a rule ends up feeding itself — an alias whose command matches its own pattern — it stops
after five hops and says so in the output rather than looping. Five is well past anything you
would compose on purpose.

### From a sequence

Sequence steps go straight to the wire, so every speedwalk you have written still behaves
exactly as it did. Prefix a step with `>` to send it through the client pipeline instead:

```
.walk >cs pause;open cask;get all;wait 2;>cs resume
```

Unprefixed steps are untouched, `wait N` still pauses (only when unprefixed — `>wait 2` asks
the client to run a command spelled `wait 2`), `>cs step x3` repeats, and `>>` is a literal `>`
for a MUD that wants one.

Macros already work this way and always have: a key binding goes through the same pipeline
typing does, so `F1 = cs pause` has never needed anything special.

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
| `.import <file>` | Read a MUSHclient world file (`.mcl`) or exported plugin and say what would come across. Add `apply` to keep it. |

### Bringing rules over from MUSHclient

`.import <path to a .mcl>` reads a MUSHclient world file and reports what it found, without
changing anything. Add `apply` to keep it:

```
.import C:\mush\worlds\3scapes.mcl
.import C:\mush\worlds\3scapes.mcl apply
```

Most of a hand-written rule set crosses unchanged — `match`, `regexp`, `sequence`, `group`,
`keep_evaluating`, `one_shot` and `omit_from_output` all mean the same thing in both clients,
MUSHclient's non-regex `*` and `?` wildcards are the ones Scrye already compiles, and `%1`–`%9`
in send text needs no rewriting. Triggers, aliases, interval timers, macros and variables all
come across, including MUSHclient's *Send to Execute* rules, which land on Scrye's **Client**
destination. Everything the file did not already put in a group is put in one named after the
file, so it is a single collapsed header in Settings and a single thing to delete if you change
your mind. Importing the same file twice updates its rules rather than doubling them.

What does **not** cross is listed, with the reason, rather than imported half-working:

- **Script rules.** The XML only names the function; the Lua lives in the plugin's `<script>`
  block and has to be ported by hand. A trigger that fires and does nothing is worse than a
  trigger you know is missing.
- **Multi-line triggers** — Scrye matches one line at a time.
- **Time-of-day timers** — Scrye timers repeat on an interval.
- **Notepad, status-line and log-file destinations**, which Scrye has no equivalent for. A
  speedwalk send is skipped too, pointing you at `.walk` and sequences instead.

Two things are imported but flagged in the report: rules using `@variable` expansion (rewrite
them as `${name}`), and regex rules with no `ignore_case` setting, which come across
case-sensitive because in this format an absent flag is a real answer. Colour triggers print
the colour they produced beside the number it came from, so you can check one against
MUSHclient's own swatch before keeping the rest.

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

## GMCP — structured data from the MUD

GMCP is out-of-band JSON: your vitals, who is attacking you, the room you are in, chat lines and
guild status, sent alongside the ordinary game text. None of it changes gameplay. Scrye
negotiates it automatically (telnet option 201) and there is nothing to turn on in the game.

**Subscribing is the part that matters**, and it is the part that is easy to get wrong. A server
that sends only the packages you asked for will send *nothing at all* to a client that agrees to
the option and then stays quiet — which looks exactly like a server that has no GMCP. So on
negotiation Scrye sends `Core.Hello` and then subscribes to the four roots 3Scapes publishes:

```
Core.Supports.Set ["Char 1","Room 1","Comm 1","Guild 1"]
```

Roots rather than exact package names, so a package added later starts arriving without a client
release. If no data has appeared a few seconds after subscribing, Scrye tries the bare
`Core.Supports` spelling once and says so in the output — 3Scapes' help text and the GMCP
specification name the mechanism slightly differently, and the retry turns a puzzling silence
into one line naming what happened.

### Seeing what actually arrives — `.gmcp`

| Command | What it does |
|---|---|
| `.gmcp` | Whether the option was negotiated, what was subscribed to, what the server answered with `Core.Supported`, and every package that has arrived with a count and a one-line sample. |
| `.gmcp <package>` | The whole of that package's last payload, pretty-printed. Case-insensitive, so `.gmcp char.vitals` is fine. |
| `.gmcp raw on` / `off` | Echo every message into the output as it lands. Noisy on purpose — this is the one to run for a few minutes the first time a feed goes live. |
| `.gmcp fields` | Write a markdown report into the log folder: every room you walked through and which area it was in, then every package with its fields, its raw payloads, and how many *different* ones it sent. The artefact worth keeping from a session — it says what the server actually sends, which is the only thing worth writing a plugin against. |

The three ways a feed can be silent — never negotiated, negotiated but never subscribed, and
subscribed but nothing has changed yet — look identical from the output pane and need different
fixes, so `.gmcp` names which one it is rather than making you guess.

### What it feeds

Every package lands in the **State** inspector under its own name (`char.vitals.hp`,
`room.info.area`), and the ones with a MIP equivalent are *also* copied onto the paths MIP
already feeds, so a HUD panel or a plugin written against `character.health.current` works from
either protocol without knowing which one it is talking to:

| GMCP | also lands at |
|---|---|
| `Char.Vitals` hp / maxhp / sp / maxsp | `character.health.current` / `.max`, `character.spell.current` / `.max` |
| `Char.Vitals` enc / coffin / coffin_max | `character.encumbrance`, `character.coffin.current` / `.max` *(no MIP equivalent)* |
| `Char.Combat` attacker / attacker_hp / rounds | `enemy.name`, `enemy.health`, `combat.round` |
| `Char.Combat` target | `combat.target` *(no MIP equivalent)* |
| `Room.Info` num / name / area | `room.num` / `.name` / `.area` |
| `Room.Info` exits | `room.exits` — the plain list, `"e,w"` |

`Room.Contents`, `Room.Map`, `Comm.Channel.Text` and the `Guild.*` packages have no MIP
counterpart and stay on their own paths; plugins read them with `scrye.onGmcp("Room.Contents",
fn)` or from the state tree.

Two things worth knowing, both from 3Scapes' own help: packages flow **only while subscribed**
and **only when their values change**, and the Room packages are sent when you *enter* a room —
`look` does not resend them. A quiet feed is often just a quiet moment.

### The feed as it actually is

Captured from a live session rather than read off the help text, because two of these are not
what the help text implies.

**`Room.Info.exits` is an object, not a string** — direction to *destination room number*:

```json
{ "num": 3872, "name": "On the outer wall", "area": "Angarboda",
  "exits": { "w": 3873, "e": 0 } }
```

That is the map graph handed over, edge by edge, which is a great deal more than the room
header ever gave. A destination of `0` still means the exit is there; the server is just not
saying where it leads, which is precisely the frontier a mapper wants to walk to. Destinations
live at `room.info.exits.<dir>` and are cleared when you leave; `room.exits` is the compact
list, in compass order.

**Not every key is a compass point, and two of them can lead to the same place.** `in`, `out`
and `enter` all turn up as exits — the gatehouse of Midgard reports
`{"nw": 50940, "sw": 50943, "in": 50943}`, where `in` goes exactly where southwest goes.
`room.exits` lists the compass points first, in compass order, then everything else
alphabetically: `sw,nw,in`.

**An empty `exits` does not mean a dead end.** Hidden exits are not included, so a room whose
exits are all hidden reports `{}` — Da Void does, and you can walk straight through it. There is
no way to learn those but to try them. Treat `{}` as "nothing to tell you", never as "nowhere
to go".

**`"Unknown"` is an answer, not a gap.** It is what the connective parts of the world report:
the realm between named areas, and the main town. In a walk across six areas, 25 of 40 rooms
came back that way. So it is worth showing — "you are out in the realm" is real information —
but it is shared by a great many rooms that have nothing to do with each other, so it can label
a room and can never key one. `num` is the identity.

One more, from the Sea of Chaos: an exit can point at **the room you are standing in**
(`{"num": 60494, "exits": {"n": 0, "e": 60494}}`). The sea is generated fresh each time and is a
fair worst case rather than a typical room — the bundled chaos-sea bot maps it by dead reckoning
for that reason and needs none of this — but a general mapper has to survive it.

### If you are building a mapper

Four rules, learned the hard way rather than read off the help text:

1. **Key on `num`.** Not the name (rooms share them — three "Mithil Stonedown Home" in one
   village) and not the area (`"Unknown"` covers half the world).
2. **A compass exit reverses; a special one does not.** North out is south back, almost
   always. `in` need not come back as `out`, and `enter` need not come back at all — record
   those one way and learn the return by walking it.
3. **`{}` means "find out yourself".** Absent exits are hidden, not missing.
4. **Two exits can share a destination, and an exit can point at its own room.** Neither is an
   error to be corrected.

`room.exits` lists the compass points first for exactly reason 2: those are the ones you may
reason about in both directions, and the rest are the ones you may not.

**`Comm.Channel.Text` carries more than the three documented fields, and not always the same
ones.** A tell you sent:

```json
{ "channel": "tell", "talker": "Lobo", "text": "ahh ok fattar",
  "prefix": "You tell Rocky:", "outgoing": 1, "targets": ["Rocky"] }
```

A channel line you received:

```json
{ "channel": "ctell", "talker": "Kimura", "text": "has reconnected.",
  "prefix": "[Corp Notify] Kimura" }
```

Only `channel`, `talker` and `text` can be relied on. The rest follow the kind of line rather
than appearing at random: `targets` is there when the line was aimed at somebody — a tell is
between two characters — and absent on a public channel like `ctell`, which has no one
recipient. `outgoing` marks lines you sent. `prefix` is the rendered line-opener and is missing
on a soul, which is its own message rather than something somebody said:

```json
{ "channel": "soul", "talker": "Ulfr", "text": "Ketilsson nods with clear respect." }
```

Between them that is enough to route and re-render chat without touching the text stream — as
long as nothing assumes a field it has not checked for.

`Room.Contents` carries a `full` flag beside its `items`, each of which has a `type`
(`"item"` or `"monster"`), a `name` and a `count`. An empty room sends `"items": []`, which has
no leaves at all — so there is no `room.contents.items` in the state tree to read, and the
previous room's contents are cleared rather than left behind.

`Room.Map` comes as `kind:"compass"` below cartography skill 1 and as `kind:"los"` once the
skill is trained. `w` and `h` give the grid's size and **`h` varies room by room** — from a
single row in a closed space to nine in the open. `up`, `down` and `enter` are flags for this
room rather than the map: `enter: 1` alongside an `enter` exit, `down: 1` alongside a `v` glyph.

**`Core.Supported` arrives twice, and the first one is every package at `0`.** The server
answers once before your subscription lands — "subscribed to nothing" — and again after it, with
each package at `1`. `.gmcp` calls it out if the *latest* answer is all zeros, because that is
what a subscription that did not take looks like, and from the output pane it is
indistinguishable from a server that has no GMCP at all.

**A monster's name is not spelled the same in every package.** `Room.Contents` gives
`"A misfigured thing {somewhat chaotic}"` and `Char.Combat` gives the same creature as
`"a misfigured thing {somewhat chaotic}"`. Match them case-insensitively, and strip the
`{…}` qualifier before putting a name in a command.

The `Guild.*` packages are advertised in `Core.Supported` for every character, but four captures
across two different guilds — including one of nearly two hours and ten thousand messages — saw
**no `Guild.*` message at all**. Treat them as announced but not yet flowing, and keep reading
guild state from MIP until one turns up.

Guild *notices* do arrive, though, as ordinary chat on their own channel:

```json
{ "channel": "vnotify", "talker": "Skadi", "prefix": "-~* Viking Notify *~-",
  "text": "Skadi has committed 9 hirdmadrs to patrol (auto-patrol)." }
```

So a guild plugin can follow what the guild is doing through `Comm.Channel.Text` today, even
with `Guild.*` silent.

**Reading a capture.** A package's message count is not the number of *things* it told you:
`Room.Info` announcing 534 times across a long walk is a few hundred rooms, most of them
announced more than once. The report counts both — "534 message(s), 187 of them different" —
and opens with the rooms it named, grouped by area, because on a capture taken to find out where
you have been that is the answer and the last payload is not.

**On volume.** In that same session `Char.Combat` was 5,769 messages and `Char.Vitals` 2,989 —
between them 84% of everything that arrived, against 534 for `Room.Info`. Whatever a plugin
hangs off `scrye.onGmcp("Char.Combat", …)` runs about once a second while you are fighting, so
keep it to reading a field and setting a flag, and do the thinking somewhere less busy.

**A value above its maximum is normal, not a glitch, and it can stay that way for good.**
A wiz boost puts you over your ceiling and drains back down as you spend it. A guild change can
strand you there permanently: spell points earned in a guild that used them, carried into one
whose abilities do not, have nothing to spend them on — a capture showed `sp` 4885 against a
`maxsp` of 53 for exactly that reason. So `cur <= max` is not a rule on any field, and "it will
even out shortly" is not a safe assumption either.

Scrye carries both numbers across unchanged. The bundled `progress` and `gauge` widgets show the
true reading in their caption — `315/45` — and draw the bar full rather than overflowing it. If
you compute a percentage yourself, clamp it: `math.min(cur / max, 1)`. And a maximum of **zero**
is a real reading too — a character with no morgue coffin reports `0/0` — so guard the divisor
rather than the numbers.

### Field paths vs state paths

`.gmcp fields` prints two columns for each field, because they are not the same string. The
**field** column is the server's own spelling, which is what would show you a `maxHP` where you
expected `maxhp`. The **state path** column is what you type into `scrye.getState`: the state
tree lowercases every key and numbers array elements with a dot, so `Room.Contents`'s
`items[0].name` is read as `room.contents.items.0.name`.

### Turning it off

Per world, in the world editor (**GMCP**, on by default). It is worth having the switch on
3Scapes specifically: GMCP and MIP carry much the same data, and turning one off is how you find
out which one a panel or a plugin is actually being driven by.

## MXP — what the server can do

MXP is markup a MUD can send inline: clickable commands, links, colours, and a few things that
reach further into the client. Scrye turns it on only when the MUD negotiates it (telnet option
91), so nothing changes on a MUD that doesn't use it. When it does, the output says
`[MXP] enabled` as you connect.

### Seeing what the server actually sends — `.mxp`

| Command | What it does |
|---|---|
| `.mxp` | Whether MXP is on for this world, whether the server negotiated it, and every tag it has sent — with a count, whether it arrived on a secure line, and whether Scrye acted on it or stripped it. |
| `.mxp raw on` / `off` | Echo every tag into the output as it arrives. |

Three kinds of silence look identical in the output pane and mean completely different things,
so the report names which one you have: MXP **turned off** for this world (the option was
refused, and nothing you see says anything about the server), **negotiated but quiet** (the
server can, and has not yet — MXP rides in the ordinary text, so look at a room with exits), and
**not negotiated at all**.

The column worth reading is the last one. Markup a client does not implement is stripped
silently — which is the right thing to do with it, and which also means "the server sends
something we ignore" reads exactly like "the server sends nothing". Each stripped tag is
something the MUD is offering that you are not getting.

The other column that earns its place is secure/open. A `<SEND>` on an ordinary line is ignored
by design (see below), so a server that never marks its lines secure produces a stream full of
link tags and not one clickable link — which looks like a broken client and is not.

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

## The plugins that ship with Scrye

Everything below comes in the box. Like any plugin, each one is opt‑in per character — turn on the
ones you want.

### Two lines: classic and GMCP

Several plugins ship twice. **3Scapes** speaks GMCP, so it gets the maintained line. **3K‑3Kingdoms**
speaks only MIP, so the older dead‑reckoning versions are kept alive beside them, frozen (bugfixes
only). The pairs answer to *different* aliases on purpose, so having both installed never causes a
collision:

| Job | GMCP line (3Scapes) | Classic line |
|---|---|---|
| Automapper | **3S Map (GMCP)** — `mapg` | **3S Map (classic)** — `map` |
| Chaos‑sea bot | **3S Chaos Sea** — `cs` | **Chaos Sea (classic)** — `csc` |
| Auto‑raid | **3S Auto‑Raid (GMCP)** — `araid` | **3S Auto‑Raid (classic)** — `araidc` |
| Viking HUD | **Viking Status / World / Kingdom** | **3S Viking Status (classic)** |

The classic mapper and chaos‑sea bot are for **3K**, or any character without GMCP. The two Viking
classics are **not** — the Viking guild doesn't exist on 3K at all; they're for a 3Scapes character
still running MIP.

The one exception is the Viking HUD: the classic and the GMCP plugins answer to the **same**
commands (`vgo`, `build`, `atrade`, …), because the classic predates the split. **Run one or the
other, never both.**

### Every alias at a glance

| Alias | Plugin |
|---|---|
| `mapg` | 3S Map (GMCP) |
| `map` | 3S Map (classic) |
| `cs` · `csc` | Chaos Sea (GMCP / classic) |
| `-` `..` `.stop` `record` `pa` … | 3S Stepper |
| `chat` | 3S Chat |
| `vitals` | 3S Vitals |
| `build` `atrade` `mkref` `mkdispatch` `mkunits` `vsk` `vstock` `vtick` `vikdump` `vplan clear` | Viking Status |
| `vgo` `vhere` `vikloc` `vnav` `vmgo` `vmrun` `vicons` | Viking World |
| `vgrudge` | Viking Kingdom |
| `cyb` | 3S Cyborg |
| `gt` · `gtsys` | 3S Gentech |
| `vfx` | Viking Effects |
| `araid` · `araidc` | Auto‑Raid (GMCP / classic) |

---

### 3S Map (GMCP) — `mapg`

The automapper for a world that gives rooms numbers. Position is never inferred, so it never
drifts: a room is the number the server states, and links are room‑number to room‑number, learned
both from what the server says and from what you actually walk. Where the two disagree it tells you
and believes the walk.

It draws **one grid per area**, plus one more for each unconnected piece of an area — which is what
makes `Unknown` (the label the MUD puts on every stretch of connective realm) readable as a handful
of small maps rather than one tangle. Coordinates on screen are a layout recomputed from the links,
never an identity.

Walks go one confirmed step at a time: the next direction is sent only when the server says you
reached the room the route expected, and anything unexpected stops the walk. Everything it ever
sends is a single bare movement word.

| Command | What it does |
|---|---|
| `mapg` (or `mapg status`) | Status — rooms known, new this session, where you are, walk progress, link disagreements, shifting exits. |
| `mapg help` | The built‑in one‑line summary of everything below. |
| `mapg on` / `mapg off` | Per‑room commentary as you walk. Default **on**. |
| `mapg areas` | Every area, with a count of rooms known in it. |
| `mapg rooms` | Every known room (capped at 40 rows). |
| `mapg rooms <text>` | The same, filtered to areas whose name contains `<text>`. |
| `mapg here` | Full detail for the room you're in — neighbours, how each is known, unexplored and shifting exits. |
| `mapg room <number>` | The same detail for any room. |
| `mapg path <number>` | Print the route there. Sends nothing. |
| `mapg go <number>` | Actually walk it. |
| `mapg stop` | Stop a walk in progress. |
| `mapg explore` | Name the nearest room that still has an unexplored exit, and print the route. |
| `mapg explore go` | Same, but walk there. |
| `mapg frontier` | Every room with an unexplored exit, and which directions. |
| `mapg map` | Describe the map you're standing on and which maps it borders. |
| `mapg maps` | Every map, with room counts and borders. |
| `mapg maps <text>` | Filtered by map name **or** by a bordering map's name. |
| `mapg name` | The current map's name (and what its auto label would be). |
| `mapg name <text>` | Rename the map you're on. `mapg name -` restores the auto label. |
| `mapg fav` | Add/remove the current map from the **Favs** tab (max 20). |
| `mapg level up` / `mapg level down` | Shift the view one level. `mapg level` follows your own level again. |
| `mapg redraw` | Lay the current map out again from its links. |
| `mapg draw on` / `mapg draw off` | Panel drawing. Default **on**. |
| `mapg shift` | List every exit marked SHIFTING. |
| `mapg shift <dir>` | Mark that exit here as shifting — an elevator or portal. Nothing is learned or routed through it and it draws as `~`. |
| `mapg shift <room> <dir>` | The same for any room. Add `off` to either form to un‑mark it. |
| `mapg forget <number>` | Drop one room from the store. |
| `mapg save` | Write the store to disk now. |
| `mapg wipe` → `mapg wipe yes` | Erase every room, map name and favourite. The confirmation only counts if it's the very next `mapg` command. |

**Shifting exits mark themselves too.** If a walk through the same exit lands you somewhere new
twice, the plugin marks it shifting on its own and stops routing through it — you ride the elevator
yourself.

**With the mouse.** Hovering a room fills the peek line. **Left‑click a room** prints the route to
it; **right‑click** offers *Walk there* / *Show route* / *Room details*. On the **Maps** and **Favs**
tabs, clicking a row walks there and right‑clicking gives the same three entries. The Maps tab also
has a search box and a rename box for the map you're standing on.

---

### 3S Map (classic) — `map`

The dead‑reckoning automapper, frozen at its long‑proven pre‑GMCP state. It learns rooms as you
walk, tracks your position by counting moves, and needs the MUD's display markers switched on (the
`aset` config). Use it on 3K, or on any character without GMCP. Old maps load exactly as they were.

Because position is *inferred* here, it can drift — and it says so (`DRIFT?`), at which point
`map set` puts you back where you actually are.

| Command | What it does |
|---|---|
| `map` | Status plus the full command list. |
| `map on` / `map off` | Auto‑mapping. Default **on**. `map on` also clears a hold another plugin placed. |
| `map area <name>` | Switch to (or create) an area. Word characters and hyphens only. |
| `map areas` | Every stored area, plus any `maps.json` seeds not yet stored. |
| `map realm fantasy\|science\|chaos` | Tag the area's realm; the panel border takes its colour. `map realm -` clears it. |
| `map set <x> <y> [z]` | Re‑seat your position by hand — the way out of a drift. |
| `map undo` | Undo the last confirmed move (last 20 kept), deleting the destination room if that arrival created it. |
| `map note <text>` / `map note -` | Attach or clear a note on this room. |
| `map flag <A-Z>` / `map flag -` | Flag this room with one letter, drawn on its tile. |
| `map find <text>` | Search room names and notes; the matches become the numbered Rooms list. |
| `map go <n>` | Walk to numbered row `<n>` of that list. |
| `map goto <x> <y> [z]` | Walk to a mapped cell, one confirmed step at a time. |
| `map stop` | Abort the walk. |
| `map link <cmd> = <x> <y> <z>` | Record a special link: sending `<cmd>` here lands you there. Prefix with an area name for a cross‑area link. |
| `map link <cmd>` | Arm it instead — the next time you send `<cmd>`, wherever you land becomes the destination. `map link -` cancels. |
| `map links` / `map unlink <cmd>` | List or remove this room's special links. |
| `map enter <area> [x y z]` | Arm an area boundary: the next command you send is the crossing. `map enter -` cancels. |
| `map back <cmd>` | After a crossing, bind `<cmd>` here as the way back — the no‑coordinates way to record a portal. |
| `map export` / `map export <name>` | Print an area as JSON, for `maps.json` or as a backup. |
| `map wipe <name> confirm` | Delete a stored area. Without `confirm` it refuses. |

**Walks stop by themselves** on a refused move, a drift disagreement, a cross‑area crossing, ten
seconds of silence, a disconnect, or the idle guard. **Combat is different** — it *pauses* the walk
and it resumes on its own once the enemy is gone. Use `map stop` if you'd rather abandon it.

**With the mouse.** Hovering peeks. **Clicking a mapped room starts a walk to it** (unlike the GMCP
mapper, which prints the route). The Rooms tab has a find box; its rows aren't clickable — use
`map go <n>`.

---

### 3S Pathfinder (Rust)

No commands and no panel. It's a route‑search engine compiled to WebAssembly, and three plugins ask
it for routes when it's loaded: **3S Map (classic)** and both **chaos‑sea** bots. Without it each
falls back to its own Lua search — the same answers, just slower once a map gets big, which on a
well‑explored sea is exactly when you notice.

---

### 3S Stepper — `-` · `..` · `record`

The area bot. It walks a recorded route, kills the mobs it meets on the way, and records new routes
as you walk them. It reads the room and its contents from GMCP where that's available, so the
`=S=` / `=M=` / `=P=` display markers aren't needed; the old triggers stay as a fallback and stand
down for any room GMCP already described.

**Walking**

| Command | What it does |
|---|---|
| `- <area>` or `walker <area>` | Start botting that area from step 1. |
| `.areas` | List every area, bundled and recorded. |
| `..` | Step, or resume — arms the bot and glances so the room's mob is spotted before you move. |
| `.pause` / `.stop` | Pause where you are / stop and save the position. |
| `.resume` | Continue from where you actually are (or restart from the saved spot if not running). |
| `.dcr` | Disconnect recovery — always restart from the saved area and position. |
| `.reset` | Back to step 1 of the current route. |
| `.tostart` | Walk the route backwards to its start room, then pause. |
| `killbot` | Stop completely. |
| `.stack <n>` | For the next `n` kills, attack and immediately move on — so the mobs pile up on you. |
| `.binfo` | The built‑in help card. |
| `.dbg on` / `.dbg off` | Verbose tracing. Default **off**. |

**Settings** — `.set <option> on|off`

| Option | Meaning | Default |
|---|---|---|
| `autoresume` | After "There is no `<mob>` here", wait a second and carry on. | **on** |
| `hardmode` | Also fight mobs the area marks as hard. | from the area |
| `loop` | At the end of the route, go back to step 1 instead of stopping. | from the area |
| `notify` | Buzz your phone on the "waiting on you" pauses. | **on** |

`hardmode` and `loop` are taken from the area each time you start one, so setting them applies to
the current run only.

**Party** — `pa <name>` adds a name, `pr <name>` removes it. Someone on the list doesn't count as
"a player is here", so the bot keeps fighting. The list is shared with the chaos‑sea bots.

**Recording**

| Command | What it does |
|---|---|
| `record <name>` | Start recording. Letters, digits and underscores, no spaces — and it can't start with a digit. |
| `record` | Status — name, steps so far, kill word, and the route. |
| `record kill <word>` | The kill word applied to every mob captured in this recording. |
| `r: <command>` | Send a command **and** record it as a non‑movement step (`r: open door`). |
| `record undo` | Drop the last step. |
| `record save` (or `record stop`) | Store it. Without a kill word it stores `CHANGEME` and warns. |
| `record cancel` | Throw it away. |
| `stepexport <name>` | Print an area as a Lua block for `3s_areas.lua`. Works for bundled areas too. |

While recording, the movement words (`n`, `north`, `ne`, `out`, `enter`, …) are passed through and
appended to the route automatically — just walk the circuit you want.

**With the mouse.** The **Bot** tab's controls are clickable words — Step, Pause/Resume, Reset,
Return, Stop, and a loop toggle — and **every area name in the list starts it**. The **Record** tab
has Save / Undo / Cancel.

---

### 3S Chaos Sea — `cs` (classic: `csc`)

The chaos‑sea explorer. It maps rooms on a 3D grid, queues the exits it hasn't walked, and
BFS‑walks to the nearest frontier room; in auto mode it fights what it meets on the way. The GMCP
version reads the room, its contents and combat from the feed and takes its arrival signal from the
MUD's own room header, so none of the `=S=` / `=M=` / `=P=` / `=A|W|I=` markers need switching on.
It also asks the server which exits are still unexplored rather than deducing it — in the sea an
exit's destination reads 0 until you have walked it, which survives the coordinate collisions a
dead‑reckoned map cannot avoid.

The classic (`csc`) is the same bot frozen at its pre‑GMCP state, for 3K and MIP‑only characters. It
needs the display markers and MIP, and glances to map the room you start in. **Every command below
works under either alias.**

| Command | What it does |
|---|---|
| `cs` | The help list. |
| `cs enable` / `cs disable` | Start/stop room parsing. Enabling also freezes the world automapper so the two don't fight. |
| `cs step` | Walk one leg toward the nearest unexplored exit. |
| `cs auto on` / `cs auto off` | Explore continuously, killing on the way. |
| `cs pause` | Pause / continue (a toggle). |
| `cs leave` | Cancel auto and walk back to the start. |
| `cs reset` | Wipe the map and all counters, back to 0 0 0. |
| `cs set <x> <y> <z>` | Override the believed position (a non‑numeric argument leaves that axis alone). |
| `cs find <x> <y> <z>` | Print the route to a coordinate. Doesn't walk it. |
| `cs kill <name>` | The mob keyword to attack. Default **mutant**. |
| `cs goal <words>` | Stop‑words — pause when an item in the room matches. Default **cask portal**. |
| `cs exclude <full mob name>` | Never attack this one. |
| `cs delay <secs>` | Pause after a killing blow before re‑reading the room. Default **2.5**, range 0.5–10. |
| `cs rest <seid> [secs]` | Rest when Seid drops below `<seid>`, for `secs` at a time. Default **off**, 60 s. |
| `cs seanum <n>` | The sea number New Sea will use (1–120). Default **1**. |
| `cs party` / `cs party <names>` / `cs party clear` | The whitelist of names that don't count as "a player is here". Comma‑separated. |
| `cs notify on` / `cs notify off` | Phone notifications. Default **on**. |
| `cs debug_all` | Dump the position and every known room. |

**New Sea is a button, not a command.** The panel's **New Sea** button loots the cask, retreats,
sets the sea number and dives again — and it only works while paused, and refuses if the sea is
under an hour old and this session started it. There's no typed equivalent.

**With the mouse.** The panel has no tabs: a status line, the map grid for your level (`@` you,
`f` frontier, `v` a down exit, `S` start, `#` explored, `.` unknown) with a legend, and buttons for
On/Off, Step, Auto, Pause, Leave, Reset, Sea# −/+ and New Sea.

---

### 3S Chat — `chat`

Collects every chat channel and tell into the **Chats** capture pane — one line per message, tagged
and coloured per channel, with a dim timestamp. The pane starts empty every time Scrye runs, so the
plugin keeps the last 100 lines itself and replays them **when it loads**, between markers — you
come back to the tail of last session rather than a blank pane. Everything is written to the
plugin's log file too.

Chat relayed from your *other* open worlds appears in the same pane tagged with the world's name —
deliberately without notifications, logging or scrollback, so a busy second world can't drown the
one you're playing.

| Command | What it does |
|---|---|
| `chat watch <name>` | Watch a name. Any message containing it gets a `*`, a notification and a beep. |
| `chat unwatch <name>` / `chat watched` | Remove one / list them. |
| `chat notify` | Show every notification setting at once. |
| `chat notify tells on\|off` | Whether a tell notifies you. Default **on**. (`chat notify tells` on its own reports rather than subscribing.) |
| `chat notify <channel>` | Subscribe a channel to notifications. Default: **none subscribed**. |
| `chat unnotify <channel>` | Unsubscribe it. |
| `chat sound on\|off` | The PC beep half only — the phone push is unaffected. Default **on**. `chat sound` alone reports. |
| `chat clear` | Clear the saved history that gets replayed at load (the pane itself is cleared from the pane). |

At most one notification fires per line: a tell beats a subscribed channel, which beats a watched
name. The old miniwindow commands (`chatwin`, `chatup`, `chatdown`, `chatend`, `chatsize`) are
swallowed with a note — the pane is a HUD pane now, and scrolls and sizes itself.

---

### 3S Vitals — `vitals`

A compact gauge stack: your vitals, plus the current enemy and its health. It works on both feeds
and figures out which bars you should have on its own.

A Viking gets HP / Seid / Vig / Rad by their own named keys. A **Cyborg** gets HP / Power / Heat —
power is the resource and heat is what stops you spending it. A **Gentech** gets HP / PU / CPC, its
two guild pools. None of these have anything in common with each other beyond the package name. **Every other guild** gets HP / SP plus its two guild
pools, labelled with the server's own names for them — so any guild's bars come out right without
the plugin having to know that guild exists. On GMCP the Viking and generic sets carry a fifth
gauge, **Coffin**; the Cyborg set carries it as its fourth.

| Command | What it does |
|---|---|
| `vitals guild auto` | Detect the bar set from the feed. **The default.** |
| `vitals guild viking` | Pin the Viking set regardless. |
| `vitals guild cyborg` | Pin the Cyborg set regardless. |
| `vitals guild gentech` | Pin the Gentech set regardless. |
| `vitals guild generic` | Pin the generic set regardless. |

The **Settings** tab has the same three as buttons, and says which set is active, which feed it's
reading, and whether that was detected or pinned.

---

## The Viking suite (3Scapes)

Four plugins where there used to be one. Status and World are the two halves of the old panel, cut
along its natural seam; Kingdom and Effects are new — the dynasty pages and the effect timer bar
were never in the classic at all. They're built to run together, and each carries its own commands.

| Plugin | Owns | Alias |
|---|---|---|
| **Viking Status** | The settlement: city, builds, production, people, trade, skills | `build` `atrade` `mkref` `vsk` … |
| **Viking World** | Everywhere you travel *to*: sea, voyage, maps, missions | `vgo` `vnav` `vmrun` `vikloc` |
| **Viking Kingdom** | The dynasty: hird, recruiting, thralls, grudges, war | `vgrudge` |
| **Viking Effects** | The status‑effect timer bar | `vfx` |

**Viking World is self‑sufficient for travel** — it owns `vgo`, the route planner and the mission
runner. What it needs **Viking Status** for is two tabs: **Mission** reads the mission list Status
publishes, and **Plan** draws the grid Status computes. Without Status those two sit empty and
everything else works.

Two more Viking plugins are documented on their own below because they pair with a frozen classic:
**Auto‑Raid** (`araid`), which dispatches your longships, and **3S Vitals**, which draws a Viking's
HP / Seid / Vig / Rad bars — and every other guild's too.

---

### Viking Status — `atrade` · `build` · `vsk` · `mkref`

The settlement half of the HUD, fed by the `Guild.*` GMCP packages. Twelve tabs: **Stats**,
**Skills**, **City**, **Builds**, **Production**, **People**, **Settlers**, **Holds**, **Trade**,
**Trade Auto**, **Trade Log** and **Feeds** (the last being the debugging window — every package
with its burst count and age, and every feed key with its value).

#### The auto‑trader — `atrade`

Runs your caravans: reads the market feed, picks what to sell where, and dispatches carts. It knows
the cartyard only releases about one caravan every three minutes, so it holds after each dispatch
and reads the yard's own "Ready in" refusal to set the clock exactly rather than spamming doomed
carts at it.

It also treats a town's demand as an **answer**: zero demand gets no cart however good the price,
and every dispatched cart provisionally debits that town locally, so a quiet stretch between feed
pushes can't stack a second caravan into demand the first already claimed.

**It always loads disarmed.** That is deliberate and not persisted — `atrade on` is a decision you
make each session.

| Command | What it does |
|---|---|
| `atrade` (or `atrade status`) | Armed state, modes, settings, warehouse fill and current mode. |
| `atrade on` / `atrade off` | Arm / disarm. |
| `atrade scalp on\|off` | Buy‑low/sell‑high arbitrage buying. Default **on**. |
| `atrade restock on\|off` | Actively buy raw materials back up to the Raw> buffer. Default **off**. |
| `atrade refined on\|off` | Also sell refined goods — bread, tools, cloth… Default **on**. |
| `atrade notify on\|off` | A phone buzz per confirmed dispatch. Default **off**. |
| `atrade exempt` | List the goods held back from trading. |
| `atrade exempt <good>` | Hold/release one good. `atrade exempt clear` releases all. |
| `atrade floor <good> <n>` | Never sell that good below `n` units in the warehouse. |
| `atrade floor <good> 0` (or `off`) | Clear that floor. `atrade floor clear` clears every one; `atrade floor` lists them. |
| `atrade floorset <good>` | Apply the Trade tab's **Floor** box to that good — the same command the right‑click menu runs. Setting the same value again clears it. |
| `atrade stats` / `atrade stats reset` | Session totals — carts each way and rough daler in and out. |
| `atrade log` | The last 15 trades. |

**When a trade is logged.** Each row is written at the moment the daler actually moves, which is
not the same moment for both sides. A **buy** pays at the yard, so it logs when the cart leaves. A
**sell** is paid when the cart reaches the town and comes home, so it's held until the feed shows
that cart back and logged then — the Trade Log lists what's still rolling and what it's worth in the
meantime, so nothing looks forgotten in between. Carts in flight survive a restart: one still out is
picked up again, and one that docked while Scrye was closed is logged and marked as such.

The daler figure is the trader's own estimate — price × units — because the feed carries no per‑cart
takings. What changed is *when* it is counted, not how exactly it is known. Manual `mkdispatch`
carts are different again: they log immediately, as `MAN`, and stay out of these totals.

A floor **raises** the category reserve, never lowers it, and a floored raw material restocks up to
its floor rather than to the Raw> buffer.

**Numbers** — `atrade <name> <n>`:

| Setting | Default | What it means |
|---|---|---|
| `keep` | 20 | Units of *every* good held back from selling (your mission reserve). |
| `stock` | 300 | The Raw> buffer — reserve kept on raw goods, and the restock target. |
| `reserve` | 5000 | Daler never spent below this. |
| `margin` | 1 | Minimum profit per unit before the scalper will buy. |
| `carts` | 0 | Max carts at once. 0 = auto, from your Trading Post tier. |
| `min` | 70 | Minimum % of cart capacity before a cart goes out. |
| `rel` | 40 | Value floor — a cart must be worth this % of the best load available. |
| `flush` | 500 | A pile this big jumps the queue and ignores the value floor. 0 = off. |
| `soft` | 70 | Warehouse fill % that enters PRESSURE: rank by biggest pile, pause scalping, drop the value floor. |
| `full` | 90 | Warehouse fill % that enters CLEARING: stop buying entirely. |
| `clear` | 25 | Minimum cart fill % while clearing (replaces `min`). |
| `escort` | 5 | Escort size per cart (1–20). |
| `yard` | 180 | Seconds held after each dispatch. A real refusal overrides it with the exact number. |

#### The build planner — `build`

| Command | What it does |
|---|---|
| `build` | Print the planner rows into the output pane (the Builds tab is the better view). |
| `build all` | Show or hide maxed (tier 5) buildings. |
| `build refresh` | Redraw from current feed values — no MUD traffic. |
| `build scan` | Read every building's tier costs and requirements from `vbuild list`, gagged, and remember them. |
| `build start <name>` | Affordability‑checked build — refuses if maxed, already building, requirements unmet, or unaffordable. |

`build scan` runs itself 20 seconds after each connect. There's no GMCP source for build costs, so
this scan is the real mechanism, not a fallback.

#### The market — `mkref` · `mkdispatch` · `mkunits`

| Command | What it does |
|---|---|
| `mkref` | Refresh prices. **Now a fallback** — with the `Guild.TradeGoods` feed live it says so and sends nothing. |
| `mkdispatch buy\|sell [qty] <good> <town>` | Send one cart by hand. `qty` defaults to the Units setting, clamped 20–1000. Good names match longest‑first (`fine furs`, `salted fish`); towns match exact, then prefix, then substring, so `lodbrok` finds Lodbrok's Hold. Logged as `MAN` and kept out of the trader's counters. |
| `mkunits` / `mkunits <n>` | Show or set the manual cart size. Default **100**. |

#### Skill Watch — `vsk`

Scans `vskills` for every skill's level, point cost and daler cost, and prices each row against your
live pools and daler. There is no GMCP package for the skill listing, so the text scan is the only
way — but the affordability half comes straight off the feed, and each pool is paired to its GXP
track by **matching values**, not by assuming the names line up.

| Command | What it does |
|---|---|
| `vsk` | Help plus a one‑line status. |
| `vsk refresh` (or `vsk scan`) | Rescan. (`vskills <tree>` typed in the game rescans just that tree.) |
| `vsk peek` | The raw last capture, with what each line parsed as. |
| `vsk feed` | Which state key each pool and daler is actually read from, and its value. |
| `vsk auto on\|off` | Rescan at login. Default **on**. |
| `vsk alert on\|off` | Notify when a skill becomes trainable. Default **off** — and it fires on the crossing only, never on the first render after login. |
| `vsk src <pool\|daler> <path>` | Read that number from a different state key. `-` clears it. |
| `vsk clear` | Forget the scanned catalogue. |

#### The rest

| Command | What it does |
|---|---|
| `vstock` | Refresh warehouse stock. **Now a fallback** — with `Guild.Warehouse` live it says so and sends nothing. |
| `vtick on` / `vtick off` | Keepalive: send `l` every five minutes. |
| `vikdump` | Print every feed key seen this session and its value. |
| `vplan clear` | Forget the tracked building placements and redraw. Doesn't touch the game. |
| `vikbar` · `viktab` · `markwin` | Swallowed with a note — panels are shown, hidden and switched in the HUD. |

#### With the mouse

**Trade tab.** Left‑click a good's **name** to hold/release it (held names go amber, floored ones
blue). Right‑click it for a menu — hold or release, set the floor, clear it. Click a **town cell**
to dispatch that good there for the configured Units; buy cells are blue, sell cells green. Click a
line in the quick‑dispatch list to send that exact cart.

**Skills tab.** Left‑click **INFO** or the skill name for `vhelp`. Right‑click either for
**Info / Train** — `vtrain` is reachable only through that menu, on purpose, because it spends
points and daler and shouldn't sit under a stray click.

**Stats tab** has a *Commit patrol* button; **Builds** has *Scan costs*; **Production** has the
stock refresh.

---

### Viking World — `vgo` · `vnav` · `vmrun` · `vikloc`

Everywhere you travel to. Six tabs: **Sea** (live voyage, the chart, resolve options, saga),
**Voyage** (boons, aids, goods, curios), **Map** (the territory grid with click‑to‑travel),
**Mission**, **Plan** (computed by Viking Status, drawn here with the other maps) and **Travel**.

Routes are **planned, not remembered**: the plugin reads the territory's edge grids from the feed
and works out the route itself, so naming a spot with `vikloc` is all it takes to be able to walk
there — the list isn't limited to places with a hand‑recorded route.

| Command | What it does |
|---|---|
| `vgo <town>` | Walk to a settlement. Matches its travel code, its full name, or any part of the name. |
| `vhere <town>` | Tell the plugin where you're standing, so routes plan from the right place. |
| `vikloc <x> <y> <name>` | Name a map cell — which is also what makes it travelable. `vikloc <x> <y>` with no name clears it. |
| `vmgo <n>` | Walk to mission `<n>`'s town and hand it in. |
| `vmrun` | Run every mission that has a route, then travel home. Typing it again stops the run. |
| `vmrun stop` | Break off. |
| `vmrun pace <secs>` | Wait between missions. Default **2**, range 0.5–30. |
| `vicons` (or `sicons`) | Toggle the drawn icons on every grid here. Default **on**. |

**Auto sea‑navigation — `vnav`.** Turned on, it tours the charted islands, wrecks and objectives
nearest‑first and resolves each node it stops at by your preference list.

| Command | What it does |
|---|---|
| `vnav on` / `vnav off` | Auto‑navigation. Default **off**. |
| `vnav resolve <list>` | The preference list. Default `hold,evade?hull<40,hunt,ration,salvage,resupply?supplies<50,plunder`. |
| `vnav resolve off` | Hold at every node and resolve them yourself. |
| `vnav resolve first` | Always take whatever the MUD offers first. |
| `…,*` at the end of a list | Last resort: answer an option set you don't recognise with its first offer. |
| `vnav reset` | Forget what's been toured, so every charted feature is a candidate again. |

The list is comma‑separated keywords, each optionally carrying a condition — `evade?hull<40` means
*evade, but only if hull is under 40*. Metrics are `hull`, `morale`, `supplies` and `stress`. The
first entry that is both offered and true is the one it takes. There's no `vnav` status command; the
Sea tab's top line always shows it.

**The options depend on the encounter**, and your list will meet sets it doesn't know. When that
happens the bot does not guess: it prints what the node actually offered, tells you none of it is in
your list, and holds there — once per node, not once per burst. Add the word and carry on, or end
your list with `*` to let it take the first offer at any node it doesn't recognise.

**With the mouse.** On the **Sea** tab, click a chart cell to queue a course to it and click a
resolve option to take it; there's a *Clear voyage queue* button. On **Map** and **Travel**, clicking
a town name walks there. Clicking a **terrain cell** travels to it if that cell is somewhere you can
go — a town, or a spot you've named with `vikloc`; anything else just tells you what's there. Both
the Sea and Map tabs carry an **Icons on/off** button (the same as `vicons`). On **Mission**,
clicking a row walks and hands in that mission; buttons cover Run all, Stop, Fetch and Submit.

---

### Viking Kingdom — `vgrudge`

The dynasty half — content the old panel never showed at all. Seven tabs: **Hird** (the roster with
levels, atk/def, loyalty, status, champions, and the bond matrix), **Recruit** (what each post wants
in a hire, the hiring hall, training, the spymaster, varangians), **Thralls**, **Grudges**,
**Kingdom** (lineage standings, trade reputation, diplomacy), **Dynasty** (house, spouse, children,
schooling, heir) and **War**.

| Command | What it does |
|---|---|
| `vgrudge` | Print the grudge board — every town with a raid cooldown and how long until it cools, soonest first. Towns not listed are ready to raid. |

That's the only command; everything else is read from the tabs. The **Grudges** tab is the other
half of auto‑raid's targeting, so it's worth a look before arming the raider.

---

### Viking Effects — `vfx`

Every active status effect as a countdown bar, scaled to the longest duration seen this session,
ticked down locally between server refreshes and sorted soonest‑first. It warns on screen — and
optionally on your phone — when something is about to drop.

| Command | What it does |
|---|---|
| `vfx` | The settings line, then the whole effect list. |
| `vfx warn <secs>` | The threshold at which an effect counts as expiring — also the red line on the bars. Default **30**. |
| `vfx notify on\|off` | Buzz the phone when something crosses that line. Default **off**. |

Bars run red at or under the warn threshold, amber up to four times it, green above. An effect that
hits zero locally shows `gone?` for a few seconds rather than vanishing, in case the server just
hasn't refreshed yet. The god's name and focus are shown without a countdown, because the server
gives the expiry as a wall‑clock time the plugin sandbox can't anchor.

---

### 3S Cyborg — `cyb`

The Cyborg guild HUD. Four tabs:

- **Status** — power against its max, stored power and regen, heat with an overheating flag,
  adrenaline, pain editor, stims, ammo and its loaded type, SI level, guild xp against the cost
  of the next step, credits, rank and time in the guild.
- **Implants** — the activated systems, two columns, sorted.
- **Chassis** — hardpoints, power grid, weapon array, machine percent, then every body slot used
  and free.
- **Combat** — kills total and this login, combat rounds, online time, the strategy order and the
  firing patterns.

| Command | What it does |
|---|---|
| `cyb` | Print the Status page into the output window — a glance without opening the panel. |

That's the only command; everything else is read from the tabs.

Two pairs are worth knowing about because they look like one number and aren't. **Power** is your
working pool (`power`/`power_max`); **Reserve** is a separate store an implant grants, shown beside
it. **Ammo** is what's in the gun and the second figure is what's in the case — when the gun empties,
the case tops it up, so the two are shown side by side and never divided. **SI** reads as
`107 -> 108   54.78%`: the percentage is progress toward the next level, and at 100% the level
ticks over. **Control** on the Chassis tab is what your active implants draw against everything you
could draw, with the remaining headroom spelled out — that's the number that tells you whether you
can switch another implant on.

---

### 3S Gentech — `gt` · `gtsys`

The Gentech guild HUD. Five tabs:

- **Status** — PU, PU store and CPC against their maxima, medkits, timeslides and their refill
  clock, capsules, guild level, the guild‑exp and echelon/Order tracks, res credits and the split that feeds them,
  attack/defence bias, efficiency.
- **Systems** — a row per implanted system: on or off, what it's set to, and its live countdown.
  Then the energy field level and the DDB / shield / synthorg loadout.
- **Progress** — division, class and rank, time in the guild and in combat, kills and deaths,
  phase rank, quest points, power slots, the passive bonus factors, and the quartermaster figures.
- **Stats** — this fight's rounds and enemy, the exp and gexp rates, lifetime totals, best and
  worst enemy by class.
- **Config** — the autoguild order, panic heal, HMS, DNA and the gen timer.

| Command | What it does |
|---|---|
| `gt` | Print the Status page into the output window. |
| `gtsys` | Print the Systems page — the one with the countdowns. |

The countdowns arrive from the server about every four seconds, so nothing is ticked locally: a
number on the Systems tab is one the server sent. A countdown at zero reads `--` rather than `0s`,
because a lapsed timer and a stopped clock look identical otherwise.

**Where your gxp goes.** Guild levels stop at **50**; past that you climb *echelons*, and echelons
in turn buy *Orders*. Earned gxp is **split** between two destinations, and you set the split in
game and can change it whenever you like:

| `Gexp split` | Where the gxp goes |
|---|---|
| **100** | all of it to **research credits** — which is what phases experiments, raising your phase rank |
| **0** | all of it to **echelons** — which accumulate toward the next Order |

The Status tab shows the setting with both halves spelled out (`100% to research credits · 0% to
echelons`), sitting directly under Res credits — one of the two destinations it routes between.

Three rows on Status track the results, and they are three different things despite looking like
the same arithmetic:

- **Guild exp** — `gexp` against `gexp + g2n`. The server never sends the threshold itself, so the
  panel adds the two numbers together to get it — in the capture that came to a round 1,000,000 —
  and shows the feed's own `g2n_pct` beside the bar as a live check that the sum is right.
  `to next` spells out what is still owed, and reads **ready** rather than `0 gexp`.
- **Echelons** — `echelon_gexp` against `echelon_required`: echelons **held** against echelons
  **needed for the next Order**. `to Order` spells out the shortfall.
- **Res credits** — the research-credit balance, fed by the split.

**Timeslides** carry a `refill` line under them: at 100% the server hands out a fresh set.

This one was built from a capture taken on **another player's character**. A field whose meaning
the capture didn't establish was shipped under the **server's own field name** with its raw value,
in a *"Not yet understood"* block on the Status tab, rather than under a label that might be a lie.
Seven started there; five have since been named by a Gentech player — `g2n` and `g2n_pct` (the gexp
still owed before the next level, and the same as a percent), `reset_pct` (the timeslide refill
clock), `phase_rank` (how far your experiments are phased — raised with research credits), and
`rush` (a healing power, so it now sits with the systems that toggle). Two are still unnamed and still in the block: **`dgexp`** and
**`illuminated`**. If you play Gentech and recognise either, that block is where to look.

---

### Auto‑Raid — `araid` (classic: `araidc`)

Dispatches your docked longships at a target town, solo or as a convoy. Auto‑targeting picks at
random among the **calm pool** — the towns within 2 heat of the lowest — and rotates on a timer, so
you don't grind one town's heat up. `keep` and `reserve` protect ships at the dock.

**It always loads disarmed**, like the trader, and that isn't persisted.

| Command | What it does |
|---|---|
| `araid` | Status — armed, target, ships, keep, reserve, convoy. |
| `araid on` / `araid off` | Arm / disarm. |
| `araid target <town>` | The manual target. |
| `araid auto on\|off` | Let it pick the town itself. Default **off**. |
| `araid pool home\|foreign` | Which group auto‑targeting raids: home (your lineage, with heat) or foreign (historical, spread at random). Default **home**. |
| `araid ships <n>` | Ships per pass. Default **2**. `araid ships all` (or `araid all`) sends as many as the dock allows. |
| `araid keep <n>` | Always leave this many docked. Default **0**. |
| `araid reserve <ship>` | Never send this one — your voyage ship. `araid reserve none` clears it. |
| `araid hold <secs>` | How long auto‑targeting sticks with a town before rotating. Default **60**. |
| `araid convoy on\|off` | Send one convoy command and let the game crew it, instead of ship by ship. Default **off**. |
| `araid targets` | The valid targets from the feed, as Home and Foreign. |
| `araid heat` | Print the per‑town heat table — the names stay clickable there. |
| `araid notify` | Show both notification settings. |
| `araid notify fleet on\|off` | Buzz when ships come home. Default **off**. |
| `araid notify send on\|off` | Buzz on every dispatch. Default **off**. |

Convoy is only used when it's on, at least two ships are wanted, **and** your reserved ship isn't
sitting in the dock — otherwise it falls back to ship‑by‑ship so the named ship can be protected.

**With the mouse.** Clicking any town name — in the panel or in `araid heat` output — targets it.
The Raid tab has toggles for arming, auto‑target, pool and convoy; the Settings tab has an input box
for each of the five values.

**The classic (`araidc`)** is the same bot on the MIP feed, with one difference: it has **no
`pool`** setting, so auto‑targeting is always the calm home town. You can still target a foreign
town by hand.

---

### 3S Viking Status (classic, MIP) — frozen

The original single‑panel Viking HUD on the MIP feed, kept for a 3Scapes character still running
MIP. Seventeen tabs in one panel — everything Status and World now hold separately, plus the build
planner, the market scanner and auto‑trader, travel and the mission runner — under the **same
commands** (`vgo`, `build`, `atrade`, `mkref`, `vmrun`, …).

That shared vocabulary is exactly why you must **enable it or the GMCP line, never both**: the
aliases collide, across Status *and* World.

It is not a full substitute for the modern set, though. The classic has no **Skills** tab and no
`vsk`, no `vstock`, and none of **Viking Kingdom**'s seven tabs — the hird, recruiting, thralls,
grudges, dynasty and war pages are content the old panel never showed.

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

### Working on a plugin where it lives

Scrye looks for plugins in two folders: the ones bundled beside the executable, and your own
under `%APPDATA%\Scrye\plugins`. **Global Settings → Plugins** adds a third of your choosing —
so a plugin you are writing can be loaded from wherever you keep it, instead of being copied
into place after every edit.

It is searched **first**, so a plugin there overrides a bundled one with the same id. That is
deliberate: a folder you deliberately pointed the client at should beat what shipped, or
pointing at it achieves nothing. The new folder is picked up by worlds you connect after
saving, so reconnect to load a change.

Everything else works as normal — every immediate subfolder holding a `plugin.json` is a
plugin, and the Plugins panel lists them alongside the rest. Only "Remove" treats them
differently: it deletes from your own `%APPDATA%` folder and never from an extra one, since a
plugin you are in the middle of editing is not something a button should delete.

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
| `button` | A clickable button. | `text`, `action = function() ... end`, `onRightClick = function() ... end` *(1.9)*, `color` *(1.13 — colours the label, so a button can show the STATE of what it controls)* |
| `buttonrow` | Several buttons side by side (equal width). | `buttons = { {text=, action=, onRightClick=, color=}, ... }` |
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
