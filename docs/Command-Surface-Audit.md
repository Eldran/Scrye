# Command surface audit — what the client accepts vs. what you can find

Every input the **client itself** handles (before anything reaches the MUD or a plugin), checked
against two questions: is it reachable from the UI, and is it written down? Anything answering
"no" twice is effectively undiscoverable — it exists only for someone who already knew.

Compiled 2026-08-14 from `WorldViewModel.SubmitText` / `TryClientCommand` and the sub-command
handlers, cross-referenced against `MainWindow.axaml` and `docs/Scrye-Guide.md`.

Plugin commands (`atrade`, `chat notify`, `vikicons`, the `.` travel language, …) are **not**
in scope here — they belong to their plugins and are documented per-plugin. This is only what
Scrye accepts with no plugins loaded.

## The gaps

### 1. Undocumented *and* invisible — the real holes

**All cleared 2026-08-14.**

| Input | What it does | Status |
|---|---|---|
| `/<lua>` | **Lua console.** Runs Lua on the session loop, e.g. `/world.AddAlias("greet", "hi *", "say hello %1")`. The one privileged prefix — gated by origin, so a phone can't run it unless granted. | **Documented** — its own guide section, with the eight `world.*` calls, the sandbox, and why it alone is gated. |
| `F11` | Toggle fullscreen. | **Documented** in the main-window key list. |
| Right-click a pane tab | Move to bottom / right / float as window / close. | **Documented** in Capture panes. A ⋮ affordance on the tab is still worth considering — right-click remains the only route. |

### 2. Documented, but no way to do it from the UI

These are all in the guide's Client commands table now, so they're findable if you read it —
but a mouse-only user can't reach them at all.

| Command | Menu candidate |
|---|---|
| `.walk <route>` | — ad-hoc by nature, fine as typed-only |
| `.seq <name>` | ~~Yes~~ — **DONE.** The sequence strip now shows a picker and a Run button when nothing is running. |
| `.stop` · `.pause` · `.resume` | **Already had UI** — the strip's Pause/Resume/Stop appear whenever a sequence is active. This entry was wrong in the first draft of this audit; the transport half existed all along, only *starting* was missing. |
| `.log` (manual start/stop) | Partly covered now by the **Log every session** profile setting; a one-off toggle has no button. Low priority. |
| `.idle` (live on/off/limit) | ~~Worth a readout~~ — **DONE.** An **Idle** toggle in the bottom bar, with the limit in its tooltip. The command stays for setting the limit, which a toggle cannot express. |
| `.ts` / `.timestamps` | ~~Yes~~ — **DONE.** An **⏱ Time** toggle in the bottom bar. |
| `.mip` | Fine as a command; it's a diagnostic. |

### 3. Already fine — reachable and written down

| Command | UI equivalent |
|---|---|
| `.companion` and its sub-commands | 📱 Companion panel (the main way in; the command is muscle memory) |
| `.tts` on/off | 🔊 TTS toggle. `stop` and `rate N` are command-only, mentioned in the toggle's tooltip. |
| `.all <command>` | ⇉ All toggle is the persistent mode; `.all` is the one-off form, noted in that tooltip. |

## Full command surface, for reference

```
/<lua>                           local Lua console (privileged; origin-gated)

.walk north;north;east x3;wait 2 ad-hoc walk
.seq <name>                      run a saved sequence
.stop | .pause | .resume         control the running walk/sequence
.log | .log html | .log off      session transcript
.all <command>                   send once to every connected world
.idle | .idle on|off | .idle <N|Nm>
.tts | .tts on|off|stop|rate <N>
.companion | status | tailscale|remote | notify|push | notify test | off|stop
.mip                             MIP feed drift audit
.ts | .timestamps                toggle the HH:mm:ss gutter
```

## Status

Done 2026-08-14:

- **Sequence picker + Run** in the existing strip, so a sequence defined in Settings can be
  started without typing.
- **⏱ Time** and **Idle** toggles in the bottom bar.
- **`mipstart` removed.** MIP arms itself on connect and re-arms on reconnect, so the manual
  handshake had no remaining use — and being the only non-prefixed client word, it was the
  only one that could shadow a MUD command. `MudSession.StartMip()` went with it rather than
  being left as dead public API; if a manual re-arm is ever wanted it belongs as `.mip start`,
  next to the audit command.

Also done: **`/<lua>`, F11 and the pane right-click menu are now documented** (§1), closing
every entry that was both invisible and unwritten.

Still open — both minor, both UI-only:

1. A **⋮ affordance on pane tabs**. Right-click is now documented but still the only route to
   move, float or close a pane.
2. A one-off **`.log` toggle** has no button. Low priority now that **Log every session** exists
   in the profile settings.

Nothing in the client's command surface is undiscoverable any more: every input is either
reachable from the UI or written down, and most are both.
