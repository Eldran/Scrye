using System.Diagnostics;
using System.Text.Json;

namespace Scrye.Companion.Server.Tailscale;

/// <summary>What we could learn about the local Tailscale node. Every field is optional:
/// Tailscale is not required to run Scrye, and its absence must never be an error.</summary>
/// <param name="Installed">The CLI was found and ran.</param>
/// <param name="Running">Backend state is "Running" — installed but logged out reports false.</param>
/// <param name="DnsName">MagicDNS name without the trailing dot, e.g. <c>desktop.tail1234.ts.net</c>.</param>
/// <param name="Detail">Human-readable note when something is missing, for display to the user.</param>
/// <param name="Login">The tailnet user this node is signed in as, e.g. <c>you@gmail.com</c>.
/// Matches the <c>Tailscale-User-Login</c> header the proxy sets, so it can be trusted
/// directly as an allow-list entry.</param>
public sealed record TailscaleStatus(
    bool Installed,
    bool Running,
    string? DnsName,
    string? Detail,
    string? Login = null)
{
    public static TailscaleStatus NotInstalled(string detail) => new(false, false, null, detail);

    /// <summary>The HTTPS URL a companion client would use once <c>tailscale serve</c> is
    /// proxying, or null when the node's name is unknown.</summary>
    public string? PublicUrl => DnsName is null ? null : $"https://{DnsName}/";
}

/// <summary>
/// Best-effort discovery of the local Tailscale node, so the desktop can print the exact
/// URL a phone should open and the exact command that makes it work.
///
/// <para>Read-only and entirely optional. It shells out to the CLI rather than talking to
/// the local API socket, because the CLI's <c>--json</c> output is a stable contract while
/// the socket is not. Any failure — not installed, not on PATH, logged out, timed out —
/// resolves to a <see cref="TailscaleStatus"/> saying so, never an exception.</para>
/// </summary>
public static class TailscaleInfo
{
    /// <summary>Where the Windows installer puts the CLI. It is not added to PATH by
    /// default, so trying the bare name alone would report "not installed" on a machine
    /// where Tailscale is running perfectly well.</summary>
    private static readonly string[] CandidatePaths =
    {
        "tailscale",                                             // PATH (Linux, macOS, some Windows setups)
        @"C:\Program Files\Tailscale\tailscale.exe",
        @"C:\Program Files (x86)\Tailscale\tailscale.exe",
        "/usr/bin/tailscale",
        "/usr/local/bin/tailscale",
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale",  // macOS app bundle
    };

    public static async Task<TailscaleStatus> QueryAsync(CancellationToken ct = default)
    {
        foreach (string exe in CandidatePaths)
        {
            (bool ok, string stdout) = await RunAsync(exe, "status --json", ct).ConfigureAwait(false);
            if (!ok) continue;
            return Parse(stdout);
        }

        return TailscaleStatus.NotInstalled("tailscale CLI not found");
    }

    /// <summary>Parse <c>tailscale status --json</c>. Separated from process launching so the
    /// shape can be tested against a fixture without Tailscale installed.</summary>
    public static TailscaleStatus Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? backend = root.TryGetProperty("BackendState", out JsonElement b) ? b.GetString() : null;
            bool running = string.Equals(backend, "Running", StringComparison.Ordinal);

            string? dns = null;
            string? login = null;
            if (root.TryGetProperty("Self", out JsonElement self))
            {
                if (self.TryGetProperty("DNSName", out JsonElement d))
                {
                    // MagicDNS names come back fully qualified with a trailing dot.
                    dns = d.GetString()?.TrimEnd('.');
                    if (string.IsNullOrWhiteSpace(dns)) dns = null;
                }

                // Self.UserID indexes into the User map, which holds the LoginName.
                if (self.TryGetProperty("UserID", out JsonElement uid) &&
                    root.TryGetProperty("User", out JsonElement users) &&
                    users.ValueKind == JsonValueKind.Object)
                {
                    string key = uid.ValueKind == JsonValueKind.Number
                        ? uid.GetRawText()
                        : uid.GetString() ?? "";
                    if (users.TryGetProperty(key, out JsonElement u) &&
                        u.TryGetProperty("LoginName", out JsonElement ln))
                    {
                        login = ln.GetString();
                        if (string.IsNullOrWhiteSpace(login)) login = null;
                    }
                }
            }

            string? detail = running
                ? (dns is null ? "MagicDNS name unavailable — is MagicDNS enabled?" : null)
                : $"tailscale is installed but not running (state: {backend ?? "unknown"})";

            return new TailscaleStatus(true, running, dns, detail, login);
        }
        catch (JsonException ex)
        {
            return new TailscaleStatus(true, false, null, "could not parse tailscale status: " + ex.Message, null);
        }
    }

    /// <summary>The command that puts Tailscale's TLS proxy in front of the loopback server.
    /// Printed rather than executed: it changes the user's tailnet configuration, and the
    /// first run opens a browser consent page to enable HTTPS certificates.</summary>
    public static string ServeCommand(int localPort) =>
        $"tailscale serve --bg --https=443 http://127.0.0.1:{localPort}";

    public static string ServeOffCommand(int localPort) =>
        $"tailscale serve --https=443 http://127.0.0.1:{localPort} off";

    private static async Task<(bool Ok, string Stdout)> RunAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? p = Process.Start(psi);
            if (p is null) return (false, "");

            // Bounded: a hung CLI must not wedge the caller's command line.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            string stdout = await p.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return p.ExitCode == 0 ? (true, stdout) : (false, "");
        }
        catch (OperationCanceledException) { return (false, ""); }
        catch (Exception) { return (false, ""); }   // not found, not executable, denied — all "no"
    }
}
