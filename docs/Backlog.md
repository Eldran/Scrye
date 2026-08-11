# Scrye — Status & Backlog

*Compiled 2026-08-11, after the phone-markup work. Three lists: what needs finishing,
what we consciously parked, and what the recent infrastructure makes newly possible.*

---

## 1 · Needs finishing now

- [x] **Commit & push.** Landed as `6df3f34` (36 files) and pushed.
- [x] **Verify the phone client after rebuild.** Confirmed working in-game 2026-08-11.
- [x] **Run the test suite once** (`dotnet test`): passed, no failures.
- [x] **Give notifications something to say.** The bundled bots now notify at their
  natural moments (see below) and the Companion panel's PLUGIN SOURCES section shows and
  toggles all of it. Trigger Notify flags remain available on top for anything custom.
- [x] **Docs catch-up** in `Scrye-Guide.md`: `chat sound` in the notification table + a
  debug-in-this-order section, the `PushOutcome` test readout, phone markup + `row`
  rendering, and the `inverse`-is-desktop-only note.

## 2 · Parked deliberately (known gaps, agreed to live with for now)

- **Barlist order parity.** The desktop refinery bars were flipped to raw-amber LEFT /
  refined-green RIGHT; the phone's `buildBarList` still draws refined first. Small fix,
  purely cosmetic, but the two hosts currently disagree.
- **Refinery quality breakdown is pointer-only.** The hover tooltip (field 6 of a barlist
  row) can't fire on a touch screen, so the phone never shows it — see list 3 for the fix.
- **Colorgrid icons & weave are desktop-only.** The phone deliberately falls back to
  letter/colour tiles (documented in the WidgetSpec). Fine until you use the maps on the
  phone a lot.
- **`inverse` markup flag is ignored on the phone.** It needs resolved base colours; the
  web client only inherits them. No bundled plugin uses it.
- ~~**Theme switches don't recolour live plugin panels.**~~ — FIXED (2026-08-11): see the
  live re-theme entry in list 3.
- **Panels can be dragged over the output/chat panes.** A clamp-to-free-space option was
  discussed and parked — the overlap is sometimes wanted.
- **CS0067 warning** (`RelayCommand<T>.CanExecuteChanged` never used) — cosmetic, harmless.

## 3 · Newly possible — ideas unlocked by recent work

*Push actually reaching the iPhone + the phone understanding markup + the 1.8 API
(events, icons, row, cell sizing) opens doors that were pointless before.*

- ~~**Plugin push notifications**~~ — DONE (2026-08-11): raid fleet-returns + dispatches,
  chaossea pauses/finds/out-of-rooms/idle-guard, stepper route-done/arrived/idle-guard,
  market per-dispatch; all reported and toggleable in the Companion panel's PLUGIN SOURCES
  section via the `plugin.<id>.notify` state convention (documented in the guide).
- **Tap-to-expand barlist rows on the phone.** The quality-breakdown text already crosses
  the wire in row field 6; a tap toggle showing it under the bar gives the phone what
  desktop hover has.
- **Phone micro-icons.** The 21-glyph vocabulary is plain SVG path data — portable to the
  web client nearly verbatim, upgrading the phone's letter-grid maps to the same terrain
  look as the desktop.
- ~~**Live re-theme of plugin panels**~~ — DONE (2026-08-11): an `IReThemable` walk swaps
  freshly-resolved immutable brushes into every token-coloured widget on `ThemeService.Changed`
  (no spec replay — no state-watch churn, works on disconnected tabs, keeps input drafts and
  tab selection). Text-widget markup re-parses, colorgrid palettes re-resolve, and the
  theme-following `list`/`table` renderer invalidates itself.
- **Custom notification sounds.** `SoundService` already resolves named `.wav` files from
  `%APPDATA%/Scrye/sounds/<mud>/` — the chat plugin could take `chat sound tell.wav` and
  distinct sounds per category (tell / watch / channel).
- **`.companion status` could show the last push outcome** — `LastError` is stored now;
  surfacing it in status would answer "did last night's notify actually send?" without a test.
- **Quick Connect recent-hosts list** — the new dialog is a natural home for the last few
  host/port combos.
