namespace Scrye.Core.Text;

/// <summary>What has been seen of one MXP tag.</summary>
/// <param name="Name">The tag name, upper-cased as the parser reads it.</param>
/// <param name="Count">How many times the server OPENED it. Closing tags are not counted:
/// they would double every paired tag and answer a question nobody asked.</param>
/// <param name="Secure">Whether it was ever seen on a line the server marked secure.</param>
/// <param name="Open">Whether it was ever seen on an ordinary, unmarked line.</param>
/// <param name="Ignored">Whether Scrye stripped it rather than acting on it.</param>
public sealed record MxpTagSeen(string Name, int Count, bool Secure, bool Open, bool Ignored);

/// <summary>
/// The MXP half of what <c>GmcpAudit</c> does for GMCP: what the server negotiated, which tags
/// it actually sends, and which of those Scrye threw away.
///
/// <para>The last of those is the reason this exists. An unrecognised tag is stripped silently
/// and correctly — that is what a client should do with markup it does not implement — but it
/// means "the MUD sends something we ignore" and "the MUD sends nothing" look identical from
/// the output pane. One of those is a feature waiting to be supported and the other is nothing
/// at all, and no amount of staring at the text tells them apart.</para>
///
/// <para>Secure mode is tallied per tag for the same reason. A <c>&lt;SEND&gt;</c> on an open
/// line is ignored by design, so a server that never marks its lines secure produces a feed
/// full of link tags and not one clickable link — which looks like a client bug and is not.</para>
/// </summary>
public sealed class MxpAudit
{
    private sealed class Entry
    {
        public int Count;
        public bool Secure;
        public bool Open;
        public bool Ignored;
    }

    private readonly Dictionary<string, Entry> _tags = new(StringComparer.Ordinal);

    /// <summary>Whether the telnet option was negotiated at all.</summary>
    public bool Negotiated { get; set; }

    /// <summary>Whether MXP is switched on for this world. False means the option was refused,
    /// so silence says nothing about the server.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Echo every tag into the output as it arrives (<c>.mxp raw on</c>).</summary>
    public bool Raw { get; set; }

    public int TagsSeen => _tags.Count;
    public int TotalTags { get; private set; }

    /// <summary>How many distinct tags arrived that Scrye does not implement.</summary>
    public int IgnoredKinds
    {
        get
        {
            int n = 0;
            foreach (Entry e in _tags.Values) if (e.Ignored) n++;
            return n;
        }
    }

    public void Observe(string name, bool secure, bool closing = false)
    {
        if (string.IsNullOrEmpty(name) || closing) return;
        TotalTags++;
        if (!_tags.TryGetValue(name, out Entry? e)) _tags[name] = e = new Entry();
        e.Count++;
        if (secure) e.Secure = true; else e.Open = true;
    }

    /// <summary>The parser fell through to its default branch: this tag was stripped.</summary>
    public void Ignored(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!_tags.TryGetValue(name, out Entry? e)) _tags[name] = e = new Entry();
        e.Ignored = true;
    }

    public void Reset()
    {
        _tags.Clear();
        TotalTags = 0;
        Negotiated = false;
    }

    /// <summary>Everything seen, busiest first.</summary>
    public IReadOnlyList<MxpTagSeen> Snapshot()
    {
        var list = new List<MxpTagSeen>(_tags.Count);
        foreach (KeyValuePair<string, Entry> kv in _tags)
            list.Add(new MxpTagSeen(kv.Key, kv.Value.Count, kv.Value.Secure, kv.Value.Open, kv.Value.Ignored));
        list.Sort((a, b) => b.Count != a.Count
            ? b.Count.CompareTo(a.Count)
            : string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>The <c>.mxp</c> report.</summary>
    public IReadOnlyList<string> Report()
    {
        var lines = new List<string> { "-- MXP --" };

        if (!Enabled)
        {
            lines.Add("  turned OFF for this world, so the option was refused.");
            lines.Add("  Nothing here says anything about what the server can do.");
            return lines;
        }
        if (!Negotiated)
        {
            lines.Add("  not negotiated: the server has not offered MXP on this connection.");
            return lines;
        }

        lines.Add("  negotiated: yes");
        if (TotalTags == 0)
        {
            lines.Add("  ...but no tag has arrived yet. MXP markup travels in the ordinary text,");
            lines.Add("  so a server can negotiate it and use it only in a few places - look at a");
            lines.Add("  room with exits, or anything that would sensibly be a link.");
            return lines;
        }

        lines.Add($"  {TotalTags} tag(s) across {TagsSeen} name(s):");
        foreach (MxpTagSeen t in Snapshot())
        {
            string mode = t.Secure && t.Open ? "both" : t.Secure ? "secure" : "open";
            lines.Add($"    <{t.Name}>".PadRight(20) + $"x{t.Count,-5} {mode,-7}"
                      + (t.Ignored ? "  IGNORED - Scrye strips this one" : ""));
        }

        if (IgnoredKinds > 0)
        {
            lines.Add($"  {IgnoredKinds} kind(s) are stripped. That is not an error - a client");
            lines.Add("  strips what it does not implement - but it is the list worth reading:");
            lines.Add("  each one is something the server offers and you are not getting.");
        }
        return lines;
    }
}
