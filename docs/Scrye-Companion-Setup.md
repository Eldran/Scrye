# Reaching Scrye from your phone

**Last updated:** 2026-08-02

The companion server binds to `127.0.0.1` and nothing else. That is intentional: it is a
socket that can send commands into a live MUD session and read private tells, so it should
not appear on a café Wi-Fi because a default was permissive.

To use it from a phone, put **Tailscale** between the two devices. Tailscale gives your PC
a private address only your own devices can reach, plus a real Let's Encrypt certificate on
a name like `desktop-abc.tail1234.ts.net` — and it **renews that certificate itself**.

That last part is the reason this route is recommended over `tailscale cert`, which hands
you certificate files to install manually. Let's Encrypt certificates expire after 90 days,
and a manually installed one would stop your phone working on a day you had forgotten all
about it.

---

## What you end up with

```
phone  ──HTTPS/WSS──▶  desktop-abc.tail1234.ts.net:443   (Tailscale's proxy, holds the TLS cert)
                                    │
                                    ▼  plain HTTP, never leaves the machine
                          127.0.0.1:4747                 (Scrye's companion server)
```

Scrye itself stays loopback-bound and learns nothing about certificates.

---

## 1. Install Tailscale on the PC

Download from <https://tailscale.com/download> and install. Sign in with whichever account
you prefer — Google, Microsoft, GitHub. The free plan covers personal use comfortably.

Confirm it is up:

```powershell
& "C:\Program Files\Tailscale\tailscale.exe" status
```

The Windows installer does not add `tailscale` to `PATH`, so use the full path (or add it
yourself). The first line of output is this machine.

## 2. Install Tailscale on the phone

App Store or Play Store, then **sign in with the same account**. Same account means same
tailnet means the two devices can see each other.

Then **switch it on.** On iOS Tailscale is a VPN profile, and installing plus signing in
does not connect it — the toggle in the app does. A phone that is signed in but toggled off
shows as `offline` in `tailscale status` on the PC, and the `.ts.net` URL simply will not
resolve, which looks exactly like a broken server.

Check the PC appears in the phone's device list, and that `tailscale status` on the PC does
*not* say the phone is offline, before continuing.

## 3. Start Scrye's companion server

In Scrye, in any connected world:

```
.companion
```

It prints the loopback URL, the token, and this world's `sessionId`. Then:

```
.companion tailscale
```

It prints your machine's tailnet name, the URL your phone will use, and the exact proxy
command for step 4. If it says Tailscale is not installed or not running, fix that first —
Scrye is reading the real state, not guessing.

## 4. Put Tailscale's TLS proxy in front

In a terminal on the PC:

```powershell
& "C:\Program Files\Tailscale\tailscale.exe" serve --bg --https=443 http://127.0.0.1:4747
```

**The first run will not succeed.** Serve has to be enabled on the tailnet once, so the
command prints a URL and stops:

```
Serve is not enabled on your tailnet.
To enable, visit:
        https://login.tailscale.com/f/serve?node=...
```

Open that URL, approve, then **run the same command again** — it completes silently the
second time. Approving also enables HTTPS certificates, which publishes your machine names
to a public certificate transparency ledger; that is inherent to Let's Encrypt rather than
something Tailscale adds.

Check it took:

```powershell
& "C:\Program Files\Tailscale\tailscale.exe" serve status
```

`--bg` keeps it running in the background; it survives reboots. To stop proxying:

```powershell
& "C:\Program Files\Tailscale\tailscale.exe" serve --https=443 http://127.0.0.1:4747 off
```

## 5. Open it on the phone

Browse to the URL from step 3 — `https://desktop-abc.tail1234.ts.net/` — paste the token,
connect, pick your session, subscribe.

A real certificate means **no browser warning**, and more importantly a genuine *secure
context*, which is what a service worker needs. That is the whole reason this step exists
before the installable PWA (design §7.1, §8.1).

---

## Troubleshooting

**The CLI says "Logged out" even though a browser said login succeeded.** A command that
merely *triggers* a login (like `serve`) prints a URL and exits immediately, so nothing is
waiting to consume the result. Use `tailscale up` instead — it blocks until the login
actually completes — and let it return on its own rather than closing the window. If that
still fails, check the Tailscale tray app is running at all (on Windows the GUI and the
background service are separate), and try an elevated PowerShell.

**Logged in but the admin console shows the device grey.** On Windows, *logged in* and
*connected* are different states, and the console's dot also lags. `tailscale status` on the
machine is authoritative; if it reports the node with a `100.x.x.x` address, you are fine.
If it says Tailscale is stopped, connect from the tray icon or run `tailscale up`.

**`tailscale` is not recognised.** The Windows installer skips `PATH`. Use the full path
`C:\Program Files\Tailscale\tailscale.exe`, or add the folder to `PATH` yourself.

**The phone reaches the URL but sees a Kestrel error, or nothing.** The companion server
is not running. Run `.companion status` in Scrye — the proxy is happy to forward to a port
with nothing behind it.

**Connects but immediately drops.** Wrong token. Each `.companion` start mints a new one,
so a token from an earlier run will not work. Re-run `.companion status` to reprint it.

**The certificate consent page never appears.** Some tailnets already have HTTPS enabled,
in which case there is nothing to consent to. Verify with `tailscale serve status`.

**Nothing resolves the `.ts.net` name.** MagicDNS is off. Enable it in the admin console
under DNS; `.companion tailscale` reports this case explicitly.

---

## What this does and does not give you

**Does:** access from anywhere the phone has a network, a real certificate with automatic
renewal, no port forwarding, no public exposure of the desktop, and no service for anyone
to operate.

**Does not:** notifications while the Scrye page is closed. A VPN keeps the *socket* alive
but the mobile OS still suspends the *app*. That needs Web Push, which the design covers in
§7.2 — and notably it also needs no hosted component, because Scrye itself can be the push
application server.

## A note on what comes next

`tailscale serve` adds identity headers to proxied requests, identifying which tailnet user
made the request. When per-device pairing lands (design §10 step 4), that is a stronger
signal than the shared token this setup uses — worth looking at before building device
authentication from scratch.

---

Sources: [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve) ·
[serve CLI reference](https://tailscale.com/docs/reference/tailscale-cli/serve) ·
[Enabling HTTPS](https://tailscale.com/docs/how-to/set-up-https-certificates)
