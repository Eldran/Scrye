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

    /// <summary>Lines included in a snapshot when a resume gap is too large (§6).</summary>
    public int SnapshotLines { get; init; } = 500;

    /// <summary>Loopback binding with a fresh 256-bit token.</summary>
    public static CompanionServerOptions CreateDefault() => new() { Token = NewToken() };

    /// <summary>A URL-safe random token. <see cref="RandomNumberGenerator"/>, not
    /// <c>Random</c> — this is a credential.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
               .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public string WebSocketUrl => $"ws://{BindAddress}:{Port}/companion";
}
