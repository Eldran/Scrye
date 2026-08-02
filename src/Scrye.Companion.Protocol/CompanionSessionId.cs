using System.Text;

namespace Scrye.Companion.Protocol;

/// <summary>
/// How a world is named on the wire. A companion client subscribes by this id and remembers
/// it between runs, so it has to be <b>stable across reconnects and desktop restarts</b> and
/// distinct for two characters on the same MUD.
///
/// <para>Derived from the profile chain — <c>"3scapes/eldran"</c> — rather than being a
/// random GUID, so it survives restarts and reads sensibly in a session picker and in logs.
/// Quick-connect worlds have no profile, so they fall back to a random id and simply cannot
/// be re-subscribed after a restart; there is nothing stable to key them to.</para>
/// </summary>
public static class CompanionSessionId
{
    /// <summary>Build an id from a profile chain. Account is included only when it adds
    /// something: two characters under one account differ by character alone, and a bare
    /// MUD-level connection is just the mud.</summary>
    public static string FromProfile(string mud, string? account, string? character)
    {
        var parts = new List<string>(3);
        AddIfPresent(parts, mud);
        AddIfPresent(parts, account);
        AddIfPresent(parts, character);
        return parts.Count == 0 ? NewEphemeral() : string.Join('/', parts);

        static void AddIfPresent(List<string> into, string? value)
        {
            string slug = Slug(value);
            if (slug.Length > 0) into.Add(slug);
        }
    }

    /// <summary>An id for a world with no profile behind it (quick-connect). Random, and
    /// therefore not resubscribable after a restart — which is honest, because nothing about
    /// such a world is persistent either.</summary>
    public static string NewEphemeral() => "quick-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>Lowercase, ASCII-safe, no separators of its own. Anything outside
    /// <c>[a-z0-9-]</c> collapses to a single hyphen so the id stays URL- and log-friendly
    /// and cannot smuggle a '/' that would fake a deeper chain.</summary>
    public static string Slug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var sb = new StringBuilder(value.Length);
        bool lastWasDash = false;
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
