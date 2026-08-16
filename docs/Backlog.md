# Scrye — known gaps and ideas

*Last reviewed 2026-08-15.*

An honest list of what Scrye does **not** do yet, and of ideas the recent work made
practical. Nothing here is a promise — it is here so you can find out what is missing
without reading the source, and so anyone who wants to contribute can see where the
loose ends are.

---

## Known gaps

**Platform**

- **macOS has no release build.** The app itself is fine — it has been compiled and run on
  macOS without problems, and every native dependency ships macOS binaries. What is missing
  is the *shipping* pipeline: handing a build to someone else needs a `.app` bundle, a
  Developer ID certificate and notarization, or Gatekeeper refuses to open it. That is the
  real cost, not the code, so releases stay Windows and Linux and macOS users build from
  source. It also sees far less use than the other two, so treat it as lightly exercised.
- **Text-to-speech is Windows-only.** It uses `System.Speech`, guarded so it declines
  rather than crashes elsewhere. macOS has `say` and Linux has `spd-say`/`espeak`, both a
  shell-out away if anyone wants it — the same shape the sound player already uses.
- **Saved auto-login passwords: Windows and Linux only.** Windows uses Credential Manager,
  Linux the Secret Service via `secret-tool` (from `libsecret-tools`). **macOS is open**: it
  wants Security.framework, because the `security` CLI takes the password in `argv`, where
  any other process can read it off `ps`. Note libsecret's own C API is variadic, which is
  why the Linux side went through the CLI rather than P/Invoke — the same reasoning applies
  to any future rewrite.

**Mobile companion vs. desktop**

- **Refinery quality *numbers* are pointer-only.** Both hosts now draw one segment per
  quality stage, so the shape of the breakdown is visible on the phone. The figures behind
  it still live in a hover tooltip, which a touch screen cannot fire. The text already
  crosses the wire in the barlist row — a tap-to-expand toggle would close this.
- **Colorgrid icons and weave are desktop-only.** The phone deliberately falls back to
  letter/colour tiles. Fine unless you use the maps on the phone a lot.
- **The `inverse` markup flag is ignored on the phone.** It needs resolved base colours and
  the web client only inherits them. No bundled plugin uses it.
- **Capture panes do not resume.** A phone that reconnects gets the main scrollback back,
  but not pane history.
- **Per-device pairing is not built.** Access is by tailnet identity (through
  `tailscale serve`) or by a shared token. A device that is on neither path has no way in.

**Desktop**

- **HUD panels can be dragged over the output and chat panes.** A clamp-to-free-space option
  was considered and parked — the overlap is sometimes what you want.

## Ideas

- **Phone micro-icons.** The 21-glyph vocabulary is plain SVG path data, portable to the web
  client nearly verbatim — it would upgrade the phone's letter-grid maps to the desktop's
  terrain look.
- **Custom notification sounds.** `SoundService` already resolves named `.wav` files from
  `%APPDATA%/Scrye/sounds/<mud>/`; the chat plugin could take `chat sound tell.wav` and use
  distinct sounds per category.
- **`.companion status` could show the last push outcome.** `LastError` is already stored;
  surfacing it would answer "did last night's notification actually send?" without a test send.
- **Quick Connect recent-hosts list.** The dialog is a natural home for the last few
  host/port combinations.
- **A `⋮` affordance on capture-pane tabs.** Right-click is documented but is still the only
  way to move, float or close a pane.
- **A one-off `.log` toggle button.** Low priority now that "Log every session" exists in the
  profile settings.
