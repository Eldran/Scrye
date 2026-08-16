<!--
  Prepended to every release by .github/workflows/release.yml; GitHub's generated commit
  summary is appended underneath. Edit this file rather than the workflow YAML — install
  instructions change more often than build steps do.

  Keep it short. Someone reading this wants to know which file to click and what to
  expect on first launch; everything else belongs in the guide.

  Two double-brace placeholders are substituted when the release is drafted:

    {{REPO}}      this repository's URL
    {{VERSION}}   the tag, e.g. v1.0.0

  Use {{REPO}} for every link. Relative links do not reliably resolve on a release page the
  way they do in a README, because a release is rendered several path segments deep.
-->

## Download

| Platform | File | Then |
|---|---|---|
| **Windows 10/11 (x64)** | `Scrye-{{VERSION}}-win-x64.zip` | Unzip anywhere, run `Scrye.App.exe` |
| **Linux (x64)** | `Scrye-{{VERSION}}-linux-x64.tar.gz` | `tar -xzf` it, run `./Scrye.App` |

Both builds are **self-contained** — no .NET install, no runtime, nothing to set up. Unzip
and run. Settings and profiles are written to your user profile, not the program folder,
so you can keep it on a USB stick or delete the folder to uninstall.

### First launch on Windows

Windows will show **"Windows protected your PC"**. These builds are not code-signed, and an
unsigned executable that no one has downloaded before always gets that screen. Click **More
info**, then **Run anyway**.

Every asset's SHA-256 is shown beside it below, and `SHA256SUMS.txt` has them all in one file
for `sha256sum -c`.

### First launch on Linux

Any desktop distro already has what Avalonia needs. A minimal or container image needs
`libx11-6`, `libice6`, `libsm6`, `libfontconfig1`, plus at least one monospaced font — the
font picker enumerates the monospaced fonts it can find and looks broken without any.

### macOS

Not released. It compiles in CI but has never actually been run, and text-to-speech and
saved passwords are unimplemented there. Build from source if you want to try it — and
please open an issue with what happened.

## What's bundled

Seven 3Scapes plugins ship in the `plugins` folder next to the executable
(`3s-chaossea`, `3s-chat`, `3s-map`, `3s-raid`, `3s-stepper`, `3s-viking-status`,
`3s-vitals`). Plugins are **opt-in
per character**, so if you play elsewhere they simply sit there unused — and they double as
worked examples if you want to write your own.

`3s-build` and `3s-market` are also in the folder, but only as notices: the build planner is
now the **Builds** tab of `3s-viking-status`, and the market scanner and auto-trader are its
**Trade** tabs. If you had either enabled before, enable `3s-viking-status` and disable them.

## Getting started

Add a world, connect, and read [the guide]({{REPO}}/blob/main/docs/Scrye-Guide.md) when you
want triggers, aliases, profiles, plugins or the phone companion. Bug reports and questions
are welcome in [Issues]({{REPO}}/issues).
