using System.Security.Cryptography;

namespace Scrye.Companion.Server;

/// <summary>
/// How the companion server listens and who it lets in.
///
/// <para>The defaults are the first-cut posture from design §10 step 2: <b>loopback only,
/// plain HTTP, one shared token</b>. That is deliberately the smallest thing that is not
/// dangerous. <c>127.0.0.1</c> is a browser <i>secure context</i>, so a PWA — service
/// worker and all — works fully during bring-up without a certificate; LAN and remote
/// access arrive with the Tailscale-issued cert in step 6, not by loosening this.</para>
/// </summary>
public sealed class CompanionServerOptions
{
    /// <summary>Interface to bind. Loopback by default and never <c>0.0.0.0</c> — an
    /// unauthenticated or newly-authenticated listener should not appear on a café Wi-Fi
    /// because a default was permissive (§7).</summary>
    public string BindAddress { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 4747;

    /// <summary>Shared bearer token. Generated per run by <see cref="CreateDefault"/> and
    /// shown in the desktop UI; the client sends it as <c>?token=</c> or an
    /// <c>Authorization: Bearer</c> header. Replaced by per-device keys at step 4 — this is
    /// a bring-up credential, not the security model.</summary>
    public required string Token { get; init; }

    /// <summary>Whether a device authenticated with <see cref="Token"/> may use the Lua
    /// console. <b>False by default</b>: "may send commands" must not imply "may run
    /// arbitrary script" (§7.3).</summary>
    public bool MayRunScripts { get; init; }

    /// <summary>
    /// Tailnet logins that may connect without a token, e.g. <c>you@gmail.com</c>. Empty
    /// disables the mechanism entirely.
    ///
    /// <para>When <c>tailscale serve</c> proxies a request it <b>strips any client-supplied
    /// identity headers and sets its own</b>, so a <c>Tailscale-User-Login</c> arriving here
    /// genuinely came from the proxy and names the tailnet user who made the request. That
    /// makes it a better credential than a shared token: nothing to type on a phone, nothing
    /// to leak into scrollback, and it is per-user rather than per-installation.</para>
    ///
    /// <para><b>The honest caveat:</b> the header is only trustworthy for traffic that
    /// actually came through the proxy, and the server cannot distinguish that from another
    /// local process connecting to the loopback port and setting the header itself. That is
    /// a smaller hole than it sounds — a hostile process running as you could read the token,
    /// read Scrye's memory, or drive the client directly — but it is why this is an explicit
    /// allow-list rather than "trust any Tailscale header".</para>
    /// </summary>
    public IReadOnlyList<string> TrustedTailnetLogins { get; init; } = Array.Empty<string>();

    /// <summary>Lines included in a snapshot when a resume gap is too large (§6).</summary>
    public int SnapshotLines { get; init; } = 500;

    /// <summary>Where the VAPID keypair lives. Null keeps it in memory only, which means
    /// every restart invalidates existing push subscriptions — fine for tests, wrong for
    /// real use (§7.2).</summary>
    public string? VapidKeyPath { get; init; }

    /// <summary>Where registered push subscriptions live. Null keeps them in memory only.</summary>
    public string? PushSubscriptionPath { get; init; }

    /// <summary>Loopback binding with a fresh 256-bit token.</summary>
    public static CompanionServerOptions CreateDefault() => new() { Token = NewToken() };

    /// <summary>A URL-safe random token. <see cref="RandomNumberGenerator"/>, not
    /// <c>Random</c> — this is a credential.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public string WebSocketUrl => $"ws://{BindAddress}:{Port}/companion";
}
