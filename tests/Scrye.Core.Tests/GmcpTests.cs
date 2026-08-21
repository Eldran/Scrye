using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Scrye.Core.Gmcp;
using Scrye.Core.Model;
using Scrye.Core.Net;
using Scrye.Core.Session;
using Scrye.Core.State;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// GMCP end to end: the server offers the option, Scrye subscribes, packages arrive, and the
/// state tree carries them on the paths everything already reads.
///
/// <para><b>The subscription is the whole thing.</b> Scrye used to answer <c>WILL GMCP</c> with
/// <c>DO GMCP</c> and then say nothing at all — which on a server that sends only what you asked
/// for is indistinguishable from a server with no GMCP. Half of what is pinned here is that a
/// handshake goes out and what is in it.</para>
///
/// <para>Payloads are the ones 3Scapes documents, field for field, so if the shipped feed turns
/// out to differ these fail with the difference rather than passing on invented data.</para>
/// </summary>
public class GmcpTests
{
    private const byte IAC = 255, WILL = 251, DO = 253, DONT = 254, SB = 250, SE = 240, GMCP = 201;

    private static byte[] Sub(string payload)
    {
        var b = new List<byte> { IAC, SB, GMCP };
        b.AddRange(Encoding.UTF8.GetBytes(payload));
        b.Add(IAC); b.Add(SE);
        return b.ToArray();
    }

    private static T Private<T>(object o, string field) =>
        (T)o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    private static void Invoke(MudSession s, string method) =>
        s.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(s, null);

    /// <summary>The negotiated → handshake hop goes through the session mailbox, which only
    /// turns with a connection loop running. Invoking the handler is the same call the loop
    /// makes; the socket is not needed for any of this.</summary>
    private static void Negotiate(MudSession s) => Invoke(s, "OnGmcpNegotiated");

    private static MudSession Connected(out TelnetLayer telnet, out List<byte> wire)
    {
        var s = new MudSession(new WorldProfile { Host = "localhost", Port = 1, EnableGmcp = true });
        telnet = Private<TelnetLayer>(s, "_telnet");
        var sent = new List<byte>();
        telnet.SendData += b => sent.AddRange(b);
        wire = sent;
        telnet.Process(new byte[] { IAC, WILL, GMCP });
        Negotiate(s);
        return s;
    }

    private static string Text(List<byte> b) => Encoding.UTF8.GetString(b.ToArray());

    // ---- negotiation --------------------------------------------------------

    [Fact]
    public void The_option_is_accepted_and_raises_the_hook_the_handshake_hangs_off()
    {
        var t = new TelnetLayer { GmcpSupported = true };
        var wire = new List<byte>();
        bool enabled = false;
        t.SendData += b => wire.AddRange(b);
        t.GmcpEnabled += () => enabled = true;

        t.Process(new byte[] { IAC, WILL, GMCP });

        Assert.Contains(DO, wire);
        Assert.True(enabled);
    }

    [Fact]
    public void Gmcp_off_refuses_the_option_outright()
    {
        // Not "negotiate and ignore": a client that takes the data and drops it still costs the
        // server the work of sending it.
        var t = new TelnetLayer { GmcpSupported = false };
        var wire = new List<byte>();
        bool enabled = false;
        t.SendData += b => wire.AddRange(b);
        t.GmcpEnabled += () => enabled = true;

        t.Process(new byte[] { IAC, WILL, GMCP });

        Assert.Contains(DONT, wire);
        Assert.DoesNotContain(DO, wire);
        Assert.False(enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_profile_switch_reaches_the_telnet_layer(bool on)
    {
        var s = new MudSession(new WorldProfile { Host = "localhost", Port = 1, EnableGmcp = on });
        Assert.Equal(on, Private<TelnetLayer>(s, "_telnet").GmcpSupported);
    }

    [Fact]
    public void A_package_with_no_payload_is_not_mangled()
    {
        var t = new TelnetLayer();
        var got = new List<(string, string)>();
        t.GmcpReceived += (p, j) => got.Add((p, j));
        t.Process(new byte[] { IAC, WILL, GMCP });
        t.Process(Sub("Core.Ping"));

        (string pkg, string json) = Assert.Single(got);
        Assert.Equal("Core.Ping", pkg);
        Assert.Equal("", json);
    }

    // ---- the handshake ------------------------------------------------------

    [Fact]
    public void Negotiating_sends_hello_and_subscribes()
    {
        MudSession s = Connected(out _, out List<byte> wire);
        string sent = Text(wire);

        Assert.Contains("Core.Hello", sent);
        Assert.Contains("\"client\":\"Scrye\"", sent);
        Assert.Contains("Core.Supports.Set", sent);
        foreach (string root in MudSession.GmcpPackages) Assert.Contains(root, sent);
        Assert.True(s.GmcpAudit.Negotiated);
        Assert.NotNull(s.GmcpAudit.SubscriptionSent);
    }

    [Fact]
    public void The_bare_supports_spelling_is_tried_after_a_few_silent_seconds()
    {
        // 3Scapes' help calls the mechanism "Core.Supports"; the GMCP specification and every
        // other client call it "Core.Supports.Set". If they are the same thing this never
        // fires. If they are not, this is the difference between one puzzled evening and one
        // line saying exactly what happened.
        MudSession s = Connected(out _, out List<byte> wire);
        wire.Clear();

        for (int i = 0; i < 4; i++) Invoke(s, "GmcpTick");
        Assert.DoesNotContain("Core.Supports", Text(wire));

        for (int i = 0; i < 3; i++) Invoke(s, "GmcpTick");
        Assert.Contains("Core.Supports ", Text(wire));
        Assert.Equal("Core.Supports", s.GmcpAudit.SubscriptionVerb);

        wire.Clear();
        for (int i = 0; i < 20; i++) Invoke(s, "GmcpTick");
        Assert.DoesNotContain("Core.Supports", Text(wire));   // only ever once
    }

    [Fact]
    public void Data_arriving_cancels_the_fallback()
    {
        MudSession s = Connected(out TelnetLayer t, out List<byte> wire);
        t.Process(Sub("Core.Supported [\"Char.Vitals 1\",\"Room.Info 1\"]"));
        wire.Clear();

        for (int i = 0; i < 20; i++) Invoke(s, "GmcpTick");

        Assert.DoesNotContain("Core.Supports", Text(wire));
        Assert.NotNull(s.GmcpAudit.Supported);
    }

    // ---- state, on the paths that already exist -----------------------------

    [Fact]
    public void Vitals_are_mirrored_onto_the_paths_MIP_feeds()
    {
        // This is what lets a HUD panel and every plugin already written against
        // character.health.current work from either protocol without knowing which.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("{\"hp\":842,\"maxhp\":900,\"sp\":120,\"maxsp\":150,\"enc\":34,\"coffin\":2,\"coffin_max\":6}"
                      .Insert(0, "Char.Vitals ")));
        StateStore st = s.GameState;

        Assert.Equal(842, st.Get("char.vitals.hp").AsNumber());        // the raw tree
        Assert.Equal(842, st.Get("character.health.current").AsNumber());
        Assert.Equal(900, st.Get("character.health.max").AsNumber());
        Assert.Equal(120, st.Get("character.spell.current").AsNumber());
        Assert.Equal(150, st.Get("character.spell.max").AsNumber());
        Assert.Equal(34, st.Get("character.encumbrance").AsNumber());
        Assert.Equal(2, st.Get("character.coffin.current").AsNumber());
        Assert.Equal(6, st.Get("character.coffin.max").AsNumber());
    }

    [Fact]
    public void Combat_mirrors_onto_the_enemy_paths()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Combat {\"attacker\":\"a grey ooze\",\"attacker_hp\":72,\"rounds\":3,\"target\":\"you\"}"));
        StateStore st = s.GameState;

        Assert.Equal("a grey ooze", st.Get("enemy.name").Text);
        Assert.Equal(72, st.Get("enemy.health").AsNumber());
        Assert.Equal(3, st.Get("combat.round").AsNumber());
        Assert.Equal("you", st.Get("combat.target").Text);   // no MIP equivalent; keeps its own path
    }

    [Fact]
    public void The_empty_end_of_combat_snapshot_clears_the_enemy_rather_than_leaving_it_stale()
    {
        // "When combat ends one empty snapshot arrives and the stream goes quiet." A mirror
        // that only ever copies would leave the last enemy sitting there for good, and every
        // consumer treats a non-empty enemy.name as "still fighting".
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Combat {\"attacker\":\"a grey ooze\",\"attacker_hp\":72,\"rounds\":3,\"target\":\"you\"}"));
        t.Process(Sub("Char.Combat {}"));

        Assert.Equal("", s.GameState.Get("enemy.name").Text);
    }

    [Fact]
    public void A_room_arrives_with_a_real_number_and_area()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"num\":18422,\"name\":\"The Carpentry Workshop\",\"area\":\"Pinnacle\",\"exits\":\"w,n,s\"}"));
        StateStore st = s.GameState;

        Assert.Equal(18422, st.Get("room.num").AsNumber());
        Assert.Equal("The Carpentry Workshop", st.Get("room.name").Text);
        Assert.Equal("Pinnacle", st.Get("room.area").Text);
        Assert.Equal("w,n,s", st.Get("room.exits").Text);
    }

    // ---- the audit behind .gmcp ---------------------------------------------

    [Fact]
    public void The_audit_counts_packages_and_keeps_the_last_of_each()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Vitals {\"hp\":842,\"maxhp\":900}"));
        t.Process(Sub("Char.Vitals {\"hp\":800,\"maxhp\":900}"));
        t.Process(Sub("Room.Info {\"num\":1,\"name\":\"A room\",\"area\":\"Here\",\"exits\":\"n\"}"));
        GmcpAudit a = s.GmcpAudit;

        Assert.Equal(2, a.PackagesSeen);
        Assert.Equal(3, a.MessagesSeen);
        Assert.Equal(2, a.Find("char.vitals")!.Count);          // findable however you type it
        Assert.Contains("800", a.Find("Char.Vitals")!.Last);    // the LAST payload, not the first
        Assert.Null(a.Find("nope"));
    }

    [Fact]
    public void The_report_tells_the_three_silences_apart()
    {
        // A package advertised but never sent, a subscription that never landed, and an option
        // that was never negotiated all look identical from the output pane, and each needs a
        // different fix. Saying which one it is IS the feature.
        var a = new GmcpAudit();
        Assert.Contains("not negotiated", string.Join("\n", a.Report()));

        a.Negotiated = true;
        Assert.Contains("NOT SENT", string.Join("\n", a.Report()));

        a.SubscriptionVerb = "Core.Supports.Set";
        a.SubscriptionSent = "[\"Char 1\"]";
        string report = string.Join("\n", a.Report());
        Assert.Contains("no answer yet", report);
        Assert.Contains("values change", report);   // quiet can be perfectly normal
    }

    [Fact]
    public void The_field_report_flattens_a_payload_to_the_paths_a_plugin_would_read()
    {
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Room.Contents",
            "{\"items\":[{\"name\":\"Birch the Handy Hippie\",\"type\":\"monster\",\"count\":1},"
            + "{\"name\":\"a plank\",\"type\":\"item\",\"count\":3}]}");
        string md = string.Join("\n", a.FieldReport("3Scapes"));

        Assert.Contains("## Room.Contents", md);
        Assert.Contains("items[0].name", md);
        Assert.Contains("Birch the Handy Hippie", md);
        Assert.Contains("```json", md);   // a summary is not evidence
    }

    [Fact]
    public void Leaves_indexes_arrays_and_admits_when_a_payload_is_not_json()
    {
        List<string> paths = GmcpAudit
            .Leaves("{\"kind\":\"los\",\"w\":3,\"rows\":[\"...\",\".@.\"]}")
            .Select(l => l.Path).ToList();

        Assert.Contains("rows[0]", paths);
        Assert.Contains("rows[1]", paths);
        Assert.Equal("(not json)", GmcpAudit.Leaves("not json at all")[0].Path);
    }

    [Theory]
    [InlineData("", "(no payload)")]
    [InlineData("garbage", "garbage")]
    public void Pretty_leaves_alone_what_it_cannot_expand(string input, string expected) =>
        Assert.Equal(expected, GmcpAudit.Pretty(input));
}
