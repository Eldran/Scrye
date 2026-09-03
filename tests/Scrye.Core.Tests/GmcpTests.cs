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

    private static void SetPrivate(object o, string field, object value) =>
        o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o, value);

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

    // ---- Comm.Channel.Text → the chat path (the MIP replacement) ------------

    [Fact]
    public void Gmcp_chat_raises_the_same_channel_event_mip_did()
    {
        MudSession s = Connected(out TelnetLayer telnet, out _);
        var got = new List<(string Ch, string Msg)>();
        s.ChannelMessage += (ch, msg) => got.Add((ch, msg));

        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"Rictor: Interesting\", \"talker\": \"Rictor\", \"prefix\": \"-~* Viking *~-\", \"channel\": \"vik\" }"));

        (string ch, string msg) = Assert.Single(got);
        Assert.Equal("vik", ch);
        Assert.Equal("Rictor: Interesting", msg);
    }

    [Fact]
    public void Gmcp_chat_names_the_talker_when_the_text_does_not()
    {
        // Channels are inconsistent: vik embeds "Rictor: neigh", gossip sends a bare "yep"
        // with the name only in `talker`. The bridge prepends the talker exactly when the
        // text does not already carry it - so vik lines stay as sent, gossip gains its
        // speaker, and a notify narrative that mentions the name mid-sentence stays whole.
        MudSession s = Connected(out TelnetLayer telnet, out _);
        var got = new List<(string Ch, string Msg)>();
        s.ChannelMessage += (ch, msg) => got.Add((ch, msg));

        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"yep\", \"talker\": \"Brynhild\", \"prefix\": \"Brynhild <Gossip>:\", \"channel\": \"gossip\" }"));
        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"Rictor: neigh\", \"talker\": \"Rictor\", \"channel\": \"vik\" }"));
        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"The Norns cut the thread. Bjorndraugr fades from the hall.\", \"talker\": \"Bjorndraugr\", \"channel\": \"vnotify\" }"));
        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"anonymous whisper\", \"channel\": \"soul\" }"));

        Assert.Equal(4, got.Count);
        Assert.Equal(("gossip", "Brynhild: yep"), got[0]);                 // named
        Assert.Equal(("vik", "Rictor: neigh"), got[1]);                    // already named
        Assert.Equal(("vnotify", "The Norns cut the thread. Bjorndraugr fades from the hall."),
                     got[2]);                                              // narrative untouched
        Assert.Equal(("soul", "anonymous whisper"), got[3]);               // no talker to add
    }

    [Fact]
    public void Gmcp_chat_yields_when_the_mip_feed_is_live()
    {
        // 3Scapes runs MIP and GMCP TOGETHER (verified live, 29 Aug 2026): both feeds carry
        // every chat line, so a session with an active MIP feed must not deliver each line
        // twice. _mipGotData is the "MIP feed is live" flag the session already keeps (set on
        // the first MIP frame, cleared per connect); the bridge yields to it. Flipped by
        // reflection here because forging a full MIP frame needs the session's generated id -
        // the flag's own set/reset paths are pinned by the MIP tests.
        MudSession s = Connected(out TelnetLayer telnet, out _);
        var got = new List<(string Ch, string Msg)>();
        s.ChannelMessage += (ch, msg) => got.Add((ch, msg));

        SetPrivate(s, "_mipGotData", true);
        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"Rictor: doubled?\", \"talker\": \"Rictor\", \"channel\": \"vik\" }"));
        Assert.Empty(got);                       // MIP owns chat: GMCP copy suppressed

        SetPrivate(s, "_mipGotData", false);     // a reconnect without MIP (ResetMipForConnect)
        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"Rictor: single\", \"talker\": \"Rictor\", \"channel\": \"vik\" }"));
        Assert.Equal(("vik", "Rictor: single"), Assert.Single(got));
    }

    [Fact]
    public void Gmcp_chat_without_a_channel_or_text_is_not_delivered()
    {
        // A payload this bridge cannot FILE is not chat it can deliver: no channel means no
        // pane to route to, no text means nothing to show. Neither may throw or fire.
        MudSession s = Connected(out TelnetLayer telnet, out _);
        int fired = 0;
        s.ChannelMessage += (_, _) => fired++;

        telnet.Process(Sub("Comm.Channel.Text { \"text\": \"orphaned\" }"));
        telnet.Process(Sub("Comm.Channel.Text { \"channel\": \"vik\" }"));
        telnet.Process(Sub("Comm.Channel.Text not json at all"));
        telnet.Process(Sub("Comm.Channel.Text [1, 2]"));

        Assert.Equal(0, fired);
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

    /// <summary>
    /// The roots themselves, pinned by name. The test above walks GmcpPackages, so it passes
    /// whatever that array happens to hold and cannot notice a root going missing - which is
    /// exactly how Merc, Craft and Mud stayed unsubscribed while every capture showed their
    /// packages at 0 and the doc comment claimed the client subscribed to everything.
    ///
    /// A root dropped here is silent in a way nothing else catches: the server simply stops
    /// sending, the inspector shows no package, and there is no error anywhere. So the list
    /// is written out longhand. Adding a root is meant to require editing this line.
    /// </summary>
    [Fact]
    public void Subscribes_to_every_root_the_server_advertises()
    {
        Assert.Equal(
            new[] { "Char 1", "Room 1", "Comm 1", "Guild 1", "Merc 1", "Craft 1", "Mud 1" },
            MudSession.GmcpPackages);

        MudSession s = Connected(out _, out List<byte> wire);
        string sent = Text(wire);
        foreach (string root in new[] { "Merc 1", "Craft 1", "Mud 1" })
            Assert.Contains(root, sent);
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
        // Verbatim from the capture, sp ABOVE maxsp and all: whatever the server means by those
        // two, the mirror's job is to carry them across unchanged, not to tidy them up.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Vitals {\"maxsp\":45,\"sp\":315,\"hp\":9576,\"coffin\":2,"
                      + "\"maxhp\":9576,\"coffin_max\":25,\"enc\":40}"));
        StateStore st = s.GameState;

        Assert.Equal(9576, st.Get("char.vitals.hp").AsNumber());        // the raw tree
        Assert.Equal(9576, st.Get("character.health.current").AsNumber());
        Assert.Equal(9576, st.Get("character.health.max").AsNumber());
        Assert.Equal(315, st.Get("character.spell.current").AsNumber());
        Assert.Equal(45, st.Get("character.spell.max").AsNumber());
        Assert.Equal(40, st.Get("character.encumbrance").AsNumber());
        Assert.Equal(2, st.Get("character.coffin.current").AsNumber());
        Assert.Equal(25, st.Get("character.coffin.max").AsNumber());
    }

    [Fact]
    public void A_paged_package_does_not_let_one_page_delete_the_last()
    {
        // The real Guild.State burst, split the way the server splits it: bars on page 1,
        // hp on page 2, points on page 3. Pruning treated every message as a whole object, so
        // page 3 deleted page 1's bars and page 1 deleted page 3's points -- which is why a
        // Viking's Seid/Vig/Rad gauges (bound to guild.state.points.*) blinked to zero while
        // HP, which comes from unpaged Char.Vitals, never moved.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Guild.State {\"page\":1,\"pages\":3,\"full\":1,\"guild\":\"viking\","
                      + "\"daler\":943976,\"bars\":{\"gp1\":5852,\"gp1_max\":7152}}"));
        t.Process(Sub("Guild.State {\"page\":2,\"pages\":3,\"full\":1,\"guild\":\"viking\","
                      + "\"hp\":{\"cur\":6312,\"max\":6333}}"));
        t.Process(Sub("Guild.State {\"page\":3,\"pages\":3,\"full\":1,\"guild\":\"viking\","
                      + "\"points\":{\"viga\":8082,\"mviga\":8082}}"));

        // every page's data is still there after the whole burst
        Assert.Equal(943976, st.Get("guild.state.daler").AsNumber());
        Assert.Equal(5852, st.Get("guild.state.bars.gp1").AsNumber());
        Assert.Equal(6312, st.Get("guild.state.hp.cur").AsNumber());
        Assert.Equal(8082, st.Get("guild.state.points.viga").AsNumber());

        // ...and the next burst's page 1 does not wipe the points it does not carry
        t.Process(Sub("Guild.State {\"page\":1,\"pages\":3,\"full\":1,\"guild\":\"viking\","
                      + "\"daler\":944000,\"bars\":{\"gp1\":5900,\"gp1_max\":7152}}"));
        Assert.Equal(8082, st.Get("guild.state.points.viga").AsNumber());
        Assert.Equal(5900, st.Get("guild.state.bars.gp1").AsNumber());   // and it still updates
    }

    [Fact]
    public void An_unpaged_partial_from_a_paged_package_also_leaves_the_rest_alone()
    {
        // Guild.State sends unpaged partial payloads too. Pruning on one of those would wipe
        // the paged keys just as surely, so the "this package is paged" latch is sticky.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Guild.State {\"page\":3,\"pages\":3,\"guild\":\"viking\","
                      + "\"points\":{\"viga\":8082}}"));
        t.Process(Sub("Guild.State {\"guild\":\"viking\",\"gxp\":{\"vitka\":68498}}"));

        Assert.Equal(8082, st.Get("guild.state.points.viga").AsNumber());
        Assert.Equal(68498, st.Get("guild.state.gxp.vitka").AsNumber());
    }

    [Fact]
    public void An_unpaged_package_still_prunes_what_it_stops_sending()
    {
        // The behaviour the pruning exists for must survive the fix. Char.Vitals is never
        // paged, so a payload that drops a field still drops it from the tree.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Char.Vitals {\"hp\":100,\"maxhp\":200,\"coffin\":5}"));
        Assert.Equal(5, st.Get("char.vitals.coffin").AsNumber());

        t.Process(Sub("Char.Vitals {\"hp\":110,\"maxhp\":200}"));
        Assert.False(st.Has("char.vitals.coffin"));
        Assert.Equal(110, st.Get("char.vitals.hp").AsNumber());
    }

    [Fact]
    public void A_snapshot_delta_package_keeps_what_a_delta_does_not_carry()
    {
        // The third shape (first seen 2 Sep 2026, the day Merc and Mud were subscribed): one
        // payload with "full":1 and every field, then deltas carrying only what changed and
        // no "full". Merc.Vitals is the live case - {hp, hp_max, stam, ap, ...} once, then
        // {stam, target_hp} every round. Whole-object pruning on the first delta deleted the
        // merc's hp from the tree, the Seid blink again on an unpaged package.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Merc.Vitals {\"stam_max\":264,\"hp_max\":19000,\"stam\":264,\"full\":1,"
                      + "\"target_hp\":0,\"hp\":19000,\"merc\":\"Stabby\",\"ap_max\":125,\"ap\":125}"));
        t.Process(Sub("Merc.Vitals {\"merc\":\"Stabby\",\"stam\":230,\"target_hp\":91,\"target\":\"Wiremouth\"}"));

        Assert.Equal(19000, st.Get("merc.vitals.hp").AsNumber());      // untouched by the delta
        Assert.Equal(230, st.Get("merc.vitals.stam").AsNumber());      // updated by it
        Assert.Equal("Wiremouth", st.Get("merc.vitals.target").Text);  // added by it
        Assert.Equal("snapshot/delta", st.MergeModeOf("Merc.Vitals"));

        // a later SNAPSHOT still replaces the tree: a field it no longer carries is gone
        t.Process(Sub("Merc.Vitals {\"full\":1,\"hp\":100,\"hp_max\":19000,\"merc\":\"Stabby\"}"));
        Assert.False(st.Has("merc.vitals.stam"));
        Assert.Equal(100, st.Get("merc.vitals.hp").AsNumber());
    }

    [Fact]
    public void Room_contents_sends_full_every_time_so_an_empty_room_still_clears()
    {
        // Room.Contents carries "full":1 on every payload, so the snapshot/delta latch must
        // not cost it the one thing pruning is FOR: the previous room's items disappearing.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Room.Contents {\"full\":1,\"items\":[{\"type\":\"monster\",\"count\":1,\"name\":\"Wiremouth guard\"}]}"));
        Assert.Equal("Wiremouth guard", st.Get("room.contents.items.0.name").Text);

        t.Process(Sub("Room.Contents {\"full\":1,\"items\":[]}"));
        Assert.False(st.Has("room.contents.items.0.name"));
    }

    [Fact]
    public void A_paged_package_stays_unpruned_whatever_its_full_flag_says()
    {
        // Guild.State pages carry "full":1 too. The page latch is the stronger claim: a page
        // is not a snapshot of the package, and pruning on it is the original blink.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Guild.State {\"page\":1,\"pages\":3,\"full\":1,\"bars\":{\"gp1\":5852}}"));
        t.Process(Sub("Guild.State {\"page\":3,\"pages\":3,\"full\":1,\"points\":{\"viga\":8082}}"));
        t.Process(Sub("Guild.State {\"page\":1,\"pages\":3,\"full\":1,\"bars\":{\"gp1\":5900}}"));

        Assert.Equal(8082, st.Get("guild.state.points.viga").AsNumber());
        Assert.Equal("paged", st.MergeModeOf("Guild.State"));
        Assert.Equal("whole", st.MergeModeOf("Char.Vitals"));
    }

    [Fact]
    public void A_package_that_never_sent_full_is_still_whole_object()
    {
        // The latch is earned by a "full", never assumed: a package whose deltas arrive
        // without one ever having been seen (a reconnect mid-stream) prunes as before, so a
        // stale field cannot outlive the payload that dropped it.
        MudSession s = Connected(out TelnetLayer t, out _);
        StateStore st = s.GameState;

        t.Process(Sub("Merc.Info {\"merc\":\"Stabby\",\"inst_level\":2,\"class\":\"offensive\"}"));
        t.Process(Sub("Merc.Info {\"merc\":\"Stabby\",\"inst_level\":3}"));
        Assert.False(st.Has("merc.info.class"));
    }

    [Fact]
    public void Mud_status_feeds_the_reboot_clock_and_the_status_row()
    {
        // The session owns the wiring: a Mud.Status payload lands in the clock, and the
        // status text is raised the moment it changes rather than on the next second tick.
        MudSession s = Connected(out TelnetLayer t, out _);
        var seen = new List<string>();
        s.RebootStatusChanged += text => seen.Add(text);

        t.Process(Sub("Mud.Status {\"full\":1,\"reboot_total\":882425,\"reboot_left\":790010,\"uptime\":92415,\"lag\":0.0}"));
        Assert.True(s.Reboot.Known);
        Assert.Equal(790010, s.Reboot.SecondsLeft);
        Assert.Contains("reboot in 9d 3h", seen);
    }

    [Fact]
    public void Combat_mirrors_onto_the_enemy_paths()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Combat {\"target\":\"you\",\"rounds\":8,"
                      + "\"attacker\":\"A giant guard manning the wall\",\"attacker_hp\":96}"));
        StateStore st = s.GameState;

        Assert.Equal("A giant guard manning the wall", st.Get("enemy.name").Text);
        Assert.Equal(96, st.Get("enemy.health").AsNumber());
        Assert.Equal(8, st.Get("combat.round").AsNumber());
        Assert.Equal("you", st.Get("combat.target").Text);   // no MIP equivalent; keeps its own path
    }

    [Fact]
    public void The_empty_end_of_combat_snapshot_clears_the_enemy_rather_than_leaving_it_stale()
    {
        // "When combat ends one empty snapshot arrives and the stream goes quiet." A mirror
        // that only ever copies would leave the last enemy sitting there for good, and every
        // consumer treats a non-empty enemy.name as "still fighting".
        // The real snapshot sends the fields PRESENT AND EMPTY rather than omitting them, which
        // is a different path through the mirror than a bare {} — an empty string is a value,
        // and a copy that only skipped nulls would carry the last enemy straight through it.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Combat {\"target\":\"you\",\"rounds\":2,"
                      + "\"attacker\":\"a misfigured thing {somewhat chaotic}\",\"attacker_hp\":59}"));
        t.Process(Sub("Char.Combat {\"target\":\"\",\"rounds\":0,\"attacker\":\"\",\"attacker_hp\":0}"));
        StateStore st = s.GameState;

        Assert.Equal("", st.Get("enemy.name").Text);
        Assert.Equal(0, st.Get("enemy.health").AsNumber());
        Assert.Equal(0, st.Get("combat.round").AsNumber());
    }

    [Fact]
    public void An_omitted_combat_payload_clears_the_enemy_too()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Char.Combat {\"attacker\":\"a grey ooze\",\"attacker_hp\":72,\"rounds\":3,\"target\":\"you\"}"));
        t.Process(Sub("Char.Combat {}"));

        Assert.Equal("", s.GameState.Get("enemy.name").Text);
    }

    // Every payload below is verbatim from a real capture (3Scapes, 2026-08-21), not from the
    // help text. Two of them differ from what the help text implied, which is the whole reason
    // for capturing before writing anything against them.

    [Fact]
    public void A_room_arrives_with_a_real_number_and_area()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"exits\":{\"w\":3873,\"e\":0},\"area\":\"Angarboda\","
                      + "\"name\":\"On the outer wall\",\"num\":3872}"));
        StateStore st = s.GameState;

        Assert.Equal(3872, st.Get("room.num").AsNumber());
        Assert.Equal("On the outer wall", st.Get("room.name").Text);
        Assert.Equal("Angarboda", st.Get("room.area").Text);
    }

    [Fact]
    public void Exits_are_a_direction_to_room_number_map_not_a_string()
    {
        // The help text says "exits" and the room header always gave "(w,n,s)", so a string was
        // the obvious guess. It is an object: which way, and WHICH ROOM that way leads to. That
        // is the whole graph, handed over, and it is what makes dead reckoning unnecessary.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"exits\":{\"w\":3873,\"e\":0},\"area\":\"Angarboda\","
                      + "\"name\":\"On the outer wall\",\"num\":3872}"));
        StateStore st = s.GameState;

        Assert.Equal(3873, st.Get("room.info.exits.w").AsNumber());
        Assert.Equal(0, st.Get("room.info.exits.e").AsNumber());   // there, but not saying where
        Assert.Equal("e,w", st.Get("room.exits").Text);            // compass order, both counted
    }

    [Fact]
    public void Exits_are_not_all_compass_points_and_need_not_lead_somewhere_different()
    {
        // The gatehouse of Midgard, verbatim: 'in' is an exit like any other, and it leads to
        // the same room the southwest exit does. A mapper that assumed one destination per
        // room, or that every key is a compass point, would be wrong about both.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"exits\":{\"nw\":50940,\"sw\":50943,\"in\":50943},"
                      + "\"area\":\"Midgard\",\"name\":\"The gatehouse of Midgard\",\"num\":50942}"));
        StateStore st = s.GameState;

        Assert.Equal("sw,nw,in", st.Get("room.exits").Text);   // compass first, then the rest
        Assert.Equal(50943, st.Get("room.info.exits.in").AsNumber());
        Assert.Equal(50943, st.Get("room.info.exits.sw").AsNumber());
        Assert.Equal("Midgard", st.Get("room.area").Text);
    }

    [Fact]
    public void The_exit_list_is_in_compass_order_and_keeps_what_the_compass_does_not_cover()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"num\":1,\"name\":\"A room\",\"area\":\"Here\","
                      + "\"exits\":{\"d\":9,\"ne\":2,\"n\":3,\"out\":4,\"w\":5}}"));

        Assert.Equal("n,ne,w,d,out", s.GameState.Get("room.exits").Text);
    }

    [Fact]
    public void Leaving_a_room_does_not_leave_its_exits_behind()
    {
        // The destinations live where SetJson put them, which is what keeps this true: a leaf
        // the new payload does not contain is removed. A hand-rolled second copy of the tree
        // would have had to remember to do that, and would not have.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"num\":1,\"name\":\"A\",\"area\":\"X\",\"exits\":{\"w\":3873,\"e\":0}}"));
        t.Process(Sub("Room.Info {\"num\":2,\"name\":\"B\",\"area\":\"X\",\"exits\":{\"n\":7}}"));
        StateStore st = s.GameState;

        Assert.False(st.Has("room.info.exits.w"));
        Assert.False(st.Has("room.info.exits.e"));
        Assert.Equal(7, st.Get("room.info.exits.n").AsNumber());
        Assert.Equal("n", st.Get("room.exits").Text);
    }

    [Fact]
    public void A_room_with_no_exits_at_all_reads_as_empty_rather_than_stale()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Info {\"num\":1,\"name\":\"A\",\"area\":\"X\",\"exits\":{\"w\":3873}}"));
        t.Process(Sub("Room.Info {\"num\":2,\"name\":\"B\",\"area\":\"X\",\"exits\":{}}"));

        Assert.Equal("", s.GameState.Get("room.exits").Text);
    }

    [Fact]
    public void The_room_contents_list_survives_the_trip_to_the_state_tree()
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Room.Contents {\"full\":1,\"items\":[{\"type\":\"monster\",\"count\":1,"
                      + "\"name\":\"A giant guard manning the wall\"}]}"));
        StateStore st = s.GameState;

        Assert.Equal("A giant guard manning the wall", st.Get("room.contents.items.0.name").Text);
        Assert.Equal("monster", st.Get("room.contents.items.0.type").Text);
        Assert.Equal(1, st.Get("room.contents.full").AsNumber());   // undocumented, but there
    }

    [Theory]
    // A soul, which carries no prefix at all — so prefix is optional too, not just the two
    // that follow the channel.
    [InlineData("{\"text\":\"Ketilsson nods with clear respect.\",\"talker\":\"Ulfr\",\"channel\":\"soul\"}",
                "soul", "Ulfr")]
    // A public channel: a talker, no single recipient, so no targets.
    [InlineData("{\"text\":\"has reconnected.\",\"talker\":\"Kimura\","
                + "\"prefix\":\"[Corp Notify] Kimura\",\"channel\":\"ctell\"}", "ctell", "Kimura")]
    public void Only_channel_talker_and_text_can_be_relied_on(string payload, string channel, string talker)
    {
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Comm.Channel.Text " + payload));
        StateStore st = s.GameState;

        Assert.Equal(channel, st.Get("comm.channel.text.channel").Text);
        Assert.Equal(talker, st.Get("comm.channel.text.talker").Text);
        Assert.NotEqual("", st.Get("comm.channel.text.text").Text);
    }

    [Fact]
    public void A_chat_line_carries_more_than_the_help_text_lists()
    {
        // Documented as { channel, talker, text }. It also carries the rendered prefix, whether
        // the line is yours, and who it was aimed at — enough to route and re-render chat
        // without touching the text stream at all.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Comm.Channel.Text {\"text\":\"ahh ok fattar\",\"prefix\":\"You tell Rocky:\","
                      + "\"outgoing\":1,\"talker\":\"Lobo\",\"targets\":[\"Rocky\"],\"channel\":\"tell\"}"));
        StateStore st = s.GameState;

        Assert.Equal("tell", st.Get("comm.channel.text.channel").Text);
        Assert.Equal("Lobo", st.Get("comm.channel.text.talker").Text);
        Assert.Equal("You tell Rocky:", st.Get("comm.channel.text.prefix").Text);
        Assert.Equal(1, st.Get("comm.channel.text.outgoing").AsNumber());
        Assert.Equal("Rocky", st.Get("comm.channel.text.targets.0").Text);
    }

    [Fact]
    public void Core_supported_is_an_object_not_a_list()
    {
        // Another one the help text left to inference: it answers with a map of package to
        // whether you are subscribed, not an array of names.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Core.Supported { \"Room.Contents\": 1, \"Char.Vitals\": 1, \"Guild.Info\": 1 }"));

        Assert.NotNull(s.GmcpAudit.Supported);
        Assert.Contains("Room.Contents", s.GmcpAudit.Supported!);
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
        Assert.Contains("items[0].name", md);                       // the server's spelling
        Assert.Contains("room.contents.items.0.name", md);          // ...and where to read it
        Assert.Contains("Birch the Handy Hippie", md);
        Assert.Contains("```json", md);   // a summary is not evidence
    }

    [Theory]
    [InlineData("Room.Contents", "items[0].name", "room.contents.items.0.name")]
    [InlineData("Char.Vitals", "maxHP", "char.vitals.maxhp")]
    [InlineData("Room.Map", "rows[2]", "room.map.rows.2")]
    public void A_field_path_and_a_state_path_are_not_the_same_string(
        string package, string field, string expected) =>
        Assert.Equal(expected, GmcpAudit.StatePath(package, field));

    // ---- what the feed DID, not just what it looks like ---------------------

    private static GmcpAudit Walked()
    {
        var a = new GmcpAudit { Negotiated = true };
        void Room(int num, string name, string area) =>
            a.Observe("Room.Info", $"{{\"num\":{num},\"name\":\"{name}\",\"area\":\"{area}\",\"exits\":{{\"n\":0}}}}");

        Room(50942, "The gatehouse of Midgard", "Midgard");
        Room(50940, "Inside the gate", "Midgard");
        Room(50942, "The gatehouse of Midgard", "Midgard");   // walked back through it
        Room(21001, "A mushroom clearing", "Smurfs");
        Room(21002, "Under a toadstool", "Smurfs");
        Room(30500, "A dusty track", "The Land");
        return a;
    }

    [Fact]
    public void Every_room_walked_through_is_kept_not_just_the_last_one()
    {
        // The report used to hold one payload per package, so a walk through three areas came
        // out as whichever room you were standing in when you ran it — 534 messages counted
        // and one of them shown. "What does this package look like" and "what did the feed do"
        // are different questions, and the second is why you take a capture.
        IReadOnlyList<GmcpAudit.RoomSeen> rooms = Walked().Rooms();

        Assert.Equal(5, rooms.Count);                                  // the repeat is one room
        Assert.Equal(new[] { "Midgard", "Midgard", "Smurfs", "Smurfs", "The Land" },
                     rooms.Select(r => r.Area));
        Assert.Equal(50942, rooms[0].Num);
        Assert.Equal("A dusty track", rooms[4].Name);
    }

    [Fact]
    public void The_report_says_which_areas_you_walked_through()
    {
        string report = string.Join("\n", Walked().Report());
        Assert.Contains("5 room(s) in 3 area(s)", report);
        Assert.Contains("The Land", report);

        string md = string.Join("\n", Walked().FieldReport("3Scapes"));
        Assert.Contains("## Rooms visited", md);
        Assert.Contains("| Smurfs | 2 |", md);
        Assert.Contains("A mushroom clearing", md);
    }

    [Fact]
    public void One_room_that_changed_is_still_one_room()
    {
        // A room can send a genuinely different payload for the same number — a gate opens and
        // the exits are not what they were. That is two payloads worth keeping and one room.
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Room.Info", "{\"num\":50942,\"name\":\"The gatehouse\",\"area\":\"Midgard\",\"exits\":{\"nw\":1}}");
        a.Observe("Room.Info", "{\"num\":50942,\"name\":\"The gatehouse\",\"area\":\"Midgard\",\"exits\":{\"nw\":1,\"in\":2}}");

        Assert.Equal(2, a.DistinctCount("Room.Info"));   // both payloads kept
        Assert.Single(a.Rooms());                        // one room walked through
    }

    [Fact]
    public void A_package_that_repeats_itself_is_counted_once_as_distinct()
    {
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Char.Vitals", "{\"hp\":100}");
        a.Observe("Char.Vitals", "{\"hp\":100}");
        a.Observe("Char.Vitals", "{\"hp\":90}");

        Assert.Equal(2, a.DistinctCount("Char.Vitals"));
        Assert.Equal(3, a.Find("Char.Vitals")!.Count);
        Assert.Contains("3 message(s), 2 of them different",
                        string.Join("\n", a.FieldReport("w")));
    }

    [Fact]
    public void A_capture_with_no_rooms_in_it_grows_no_rooms_section()
    {
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Char.Vitals", "{\"hp\":100}");
        Assert.DoesNotContain("Rooms visited", string.Join("\n", a.FieldReport("w")));
        Assert.DoesNotContain("room(s) in", string.Join("\n", a.Report()));
    }

    [Fact]
    public void An_all_zero_supported_answer_is_called_out_as_subscribed_to_nothing()
    {
        // The server answers TWICE — once before the subscription with every package at 0, and
        // once after with them at 1. That first answer is what "negotiated but subscribed to
        // nothing" looks like from the wire, and if it is ever the LAST one, nothing will
        // arrive and the output pane will look exactly like a server with no GMCP.
        MudSession s = Connected(out TelnetLayer t, out _);
        t.Process(Sub("Core.Supported { \"Room.Info\": 0, \"Char.Vitals\": 0 }"));
        Assert.Contains("SUBSCRIBED TO NOTHING", string.Join("\n", s.GmcpAudit.Report()));

        t.Process(Sub("Core.Supported { \"Room.Info\": 1, \"Char.Vitals\": 1 }"));
        Assert.DoesNotContain("SUBSCRIBED TO NOTHING", string.Join("\n", s.GmcpAudit.Report()));
    }

    [Theory]
    [InlineData("{ \"A\": 0, \"B\": 0 }", true)]
    [InlineData("{ \"A\": 0, \"B\": 1 }", false)]
    [InlineData("{ }", false)]                      // nothing said is not "nothing on"
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Subscribed_to_nothing_is_every_package_at_zero_and_at_least_one_package(
        string? payload, bool expected) =>
        Assert.Equal(expected, GmcpAudit.SubscribedToNothing(payload));

    [Fact]
    public void An_empty_array_is_shown_without_a_state_path_it_does_not_have()
    {
        // A room with nothing in it sends "items": []. That contributes no leaves, so the
        // state tree holds no room.contents.items at all — and a report that printed the path
        // anyway would be inviting you to read something that is not there.
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Room.Contents", "{\"full\":1,\"items\":[]}");
        string md = string.Join("\n", a.FieldReport("3Scapes"));

        Assert.Contains("(empty array)", md);
        Assert.DoesNotContain("`room.contents.items`", md);
        Assert.Contains("`room.contents.full`", md);      // the real leaf still points somewhere
    }

    [Fact]
    public void A_pipe_in_the_data_is_carried_across_rather_than_corrupted()
    {
        // Room.Map has '|' as a legend KEY and as its value, and draws its rows with it. The
        // first version of this report replaced a pipe with a backslash, which kept the table
        // intact by quietly changing the data -- in a document whose whole purpose is to say
        // what the server actually sent.
        var a = new GmcpAudit { Negotiated = true };
        a.Observe("Room.Map", "{\"rows\":[\"  |  \"],\"legend\":{\"|\":\"link\"}}");
        string md = string.Join("\n", a.FieldReport("3Scapes"));

        Assert.DoesNotContain("\\|", md);          // no backslash escape, honoured or not
        Assert.Contains("&#124;", md);            // the pipe is there, and rendered as one
        Assert.Contains("\"|\": \"link\"", md);     // ...and the raw payload is untouched

        foreach (string line in md.Split('\n'))
            if (line.StartsWith("| `"))
                Assert.Equal(4, line.Split('|').Length - 1);   // three columns, no broken rows
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
