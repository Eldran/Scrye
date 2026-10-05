using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Channels;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The idle guard's hard stop (Joakim, 5 Oct 2026: "if the idle guard triggers ... it disables
/// the plugins so a bot won't still continue on?"). Once it fires, plugins are told first and
/// then held: what they send is dropped, and the hold lifts the moment you are back.
/// </summary>
public class IdleHoldTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    private static (MudSession Session, List<string> Echoes) Fresh(bool hold)
    {
        var session = new MudSession(new WorldProfile { Name = "t", Host = "localhost", Port = 1 });
        session.IdleGuard.Seconds = IdleGuard.MinSeconds;
        session.IdleGuard.Enabled = true;
        session.IdleGuard.HoldPlugins = hold;
        var echoes = new List<string>();
        session.LineReady += l => echoes.Add(l.PlainText);
        Drain(session);
        return (session, echoes);
    }

    private static void TickUntilFired(MudSession s)
    {
        MethodInfo tick = typeof(MudSession).GetMethod("TickIdleGuard", Priv)!;
        for (int i = 0; i < IdleGuard.MinSeconds && !s.IdleGuard.HasFired; i++) tick.Invoke(s, null);
        Assert.True(s.IdleGuard.HasFired);
    }

    private static void TickOnce(MudSession s) =>
        typeof(MudSession).GetMethod("TickIdleGuard", Priv)!.Invoke(s, null);

    /// <summary>What reached the session's mailbox as text for the MUD, emptying it.</summary>
    private static List<string> Drain(MudSession s)
    {
        var mailbox = (Channel<SessionMessage>)typeof(MudSession).GetField("_mailbox", Priv)!.GetValue(s)!;
        var sent = new List<string>();
        while (mailbox.Reader.TryRead(out SessionMessage? m))
            if (m is SessionMessage.SendText t) sent.Add(t.Text);
        return sent;
    }

    private static void Type(MudSession s, string text, IdleSource source = IdleSource.Keyboard) =>
        typeof(MudSession).GetMethod("HandleInput", Priv)!.Invoke(s, new object[] { text, true, source });

    [Fact]
    public void Plugin_sends_go_out_normally_before_the_guard_fires()
    {
        var (s, echoes) = Fresh(hold: true);
        s.SendFromPlugin("kill rat");
        Assert.False(s.PluginsHeld);
        Assert.Equal(new[] { "kill rat" }, Drain(s));
        Assert.Empty(echoes);
    }

    [Fact]
    public void Firing_holds_plugins_and_drops_what_they_send_saying_so_once()
    {
        var (s, echoes) = Fresh(hold: true);
        bool heldWhenTold = true;
        s.IdleSignal += sig => { if (sig == IdleGuardSignal.Fired) heldWhenTold = s.PluginsHeld; };
        TickUntilFired(s);

        Assert.False(heldWhenTold);                // onIdle runs before the hold, so a last "stop" gets out
        Assert.True(s.PluginsHeld);
        s.SendFromPlugin("l");
        s.SendFromPlugin("kill rat");
        Assert.Empty(Drain(s));
        Assert.Single(echoes, e => e.Contains("holding plugin commands") && e.Contains("'l'"));
    }

    [Fact]
    public void Coming_back_releases_the_hold_and_counts_what_was_held()
    {
        var (s, echoes) = Fresh(hold: true);
        TickUntilFired(s);
        s.SendFromPlugin("l");
        s.SendFromPlugin("l");
        Drain(s);

        Type(s, "look");
        Assert.False(s.PluginsHeld);
        Assert.Contains(echoes, e => e.Contains("2 plugin command(s) were held"));

        s.SendFromPlugin("north");
        Assert.Contains("north", Drain(s));
    }

    [Fact]
    public void A_source_that_does_not_count_does_not_release_the_hold()
    {
        var (s, _) = Fresh(hold: true);
        s.IdleGuard.Sources = IdleSource.Keyboard;
        TickUntilFired(s);

        Type(s, "north", IdleSource.Phone);
        Assert.True(s.PluginsHeld);
        Type(s, "look", IdleSource.Keyboard);
        Assert.False(s.PluginsHeld);
    }

    [Fact]
    public void Switching_the_guard_off_while_fired_releases_the_hold()
    {
        var (s, _) = Fresh(hold: true);
        TickUntilFired(s);
        Assert.True(s.PluginsHeld);

        s.IdleGuard.Enabled = false;
        TickOnce(s);
        Assert.False(s.PluginsHeld);
        s.SendFromPlugin("l");
        Assert.Equal(new[] { "l" }, Drain(s));
    }

    [Fact]
    public void With_the_hold_off_firing_leaves_plugin_sends_alone()
    {
        var (s, echoes) = Fresh(hold: false);
        TickUntilFired(s);
        Assert.False(s.PluginsHeld);
        s.SendFromPlugin("l");
        Assert.Equal(new[] { "l" }, Drain(s));
        Assert.DoesNotContain(echoes, e => e.Contains("holding plugin commands"));
    }

    [Fact]
    public void The_hold_is_on_unless_a_profile_says_otherwise()
    {
        Assert.True(new IdleGuard().HoldPlugins);
        var g = new Scrye.Core.Profiles.ProfileLayer { Kind = Scrye.Core.Profiles.LayerKind.Global, Name = "g" };
        var c = new Scrye.Core.Profiles.ProfileLayer { Kind = Scrye.Core.Profiles.LayerKind.Character, Name = "c", IdleGuardHoldPlugins = false };
        Assert.True(Scrye.Core.Profiles.ProfileResolver.Resolve(new[] { g }).IdleGuardHoldPlugins);
        Assert.False(Scrye.Core.Profiles.ProfileResolver.Resolve(new[] { g, c }).IdleGuardHoldPlugins);
    }
}
