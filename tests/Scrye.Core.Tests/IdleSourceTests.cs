using System;
using System.Reflection;
using Scrye.Core.Model;
using Scrye.Core.Profiles;
using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// What counts as you being here (Joakim, 5 Oct 2026: "choose what resets the guard so if
/// someone only wants the keyboard to count that's an option"). A source that is switched off
/// leaves the clock running and is remembered as ignored, so a guard that "reset by itself"
/// can name what did it.
/// </summary>
public class IdleSourceTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private static IdleGuard On(int seconds) => new() { Seconds = seconds, Enabled = true };

    [Fact]
    public void Everything_counts_by_default_as_it_always_did()
    {
        IdleGuard g = On(100);
        Assert.Equal(IdleSource.All, g.Sources);
        foreach (var e in IdleSources.Each) Assert.True(g.Counts(e.Source));
    }

    [Fact]
    public void A_source_that_is_off_leaves_the_clock_running()
    {
        IdleGuard g = On(100);
        g.Sources = IdleSource.Keyboard;
        for (int i = 0; i < 50; i++) g.Tick(1.0);

        Assert.False(g.NoteActivity(IdleSource.Phone, "north", T));
        Assert.Equal(50, g.IdleSeconds);
        Assert.Equal(IdleSource.Phone, g.LastIgnored!.Value.Source);
        Assert.Null(g.LastCounted);

        Assert.True(g.NoteActivity(IdleSource.Keyboard, "look", T));
        Assert.Equal(0, g.IdleSeconds);
        Assert.Equal("look", g.LastCounted!.Value.Detail);
    }

    [Fact]
    public void A_fired_guard_stays_fired_for_an_ignored_source()
    {
        IdleGuard g = On(60);
        g.Sources = IdleSource.Keyboard | IdleSource.Macro;
        for (int i = 0; i < 60; i++) g.Tick(1.0);
        Assert.True(g.HasFired);

        g.NoteActivity(IdleSource.Broadcast, "say hi", T);
        Assert.True(g.HasFired);
        g.NoteActivity(IdleSource.Macro, "F1", T);
        Assert.False(g.HasFired);
    }

    [Fact]
    public void Nothing_counting_means_it_fires_whatever_you_do()
    {
        IdleGuard g = On(60);
        g.Sources = IdleSource.None;
        foreach (var e in IdleSources.Each) Assert.False(g.NoteActivity(e.Source, "x", T));
        for (int i = 0; i < 60; i++) g.Tick(1.0);
        Assert.True(g.HasFired);
    }

    [Fact]
    public void A_long_detail_is_shortened_for_the_display()
    {
        IdleGuard g = On(100);
        g.NoteActivity(IdleSource.Keyboard, new string('x', 200), T);
        Assert.Equal(40, g.LastCounted!.Value.Detail.Length);
    }

    [Fact]
    public void The_session_passes_on_where_input_came_from()
    {
        var session = new MudSession(new WorldProfile { Name = "t", Host = "localhost", Port = 1 });
        session.IdleGuard.Enabled = true;
        session.IdleGuard.Sources = IdleSource.Keyboard;
        session.IdleGuard.Tick(30);
        MethodInfo handle = typeof(MudSession).GetMethod("HandleInput", BindingFlags.NonPublic | BindingFlags.Instance)!;

        handle.Invoke(session, new object[] { "north", true, IdleSource.Phone });
        Assert.Equal(30, session.IdleGuard.IdleSeconds);          // the phone does not count here
        handle.Invoke(session, new object[] { "look", true, IdleSource.Keyboard });
        Assert.Equal(0, session.IdleGuard.IdleSeconds);
        Assert.Equal("look", session.IdleGuard.LastCounted!.Value.Detail);
        Assert.Equal("north", session.IdleGuard.LastIgnored!.Value.Detail);
    }

    [Theory]
    [InlineData(IdleSource.All, "all")]
    [InlineData(IdleSource.None, "none")]
    [InlineData(IdleSource.Keyboard | IdleSource.Macro, "keyboard, macro")]
    [InlineData(IdleSource.Phone | IdleSource.Broadcast, "phone, broadcast")]
    public void Sources_round_trip_as_the_profile_stores_them(IdleSource s, string text)
    {
        Assert.Equal(text, IdleSources.Format(s));
        Assert.Equal(s, IdleSources.Parse(text));
    }

    [Fact]
    public void Parsing_is_forgiving_and_blank_inherits()
    {
        Assert.Equal(IdleSource.Keyboard | IdleSource.OutputLink, IdleSources.Parse(" Keyboard;outputlink  bogus "));
        Assert.Null(IdleSources.Parse(""));
        Assert.Null(IdleSources.Parse("bogus"));
        Assert.Null(IdleSources.Parse(null));
    }

    [Fact]
    public void The_deepest_layer_that_says_wins_and_none_saying_means_all()
    {
        var g = new ProfileLayer { Kind = LayerKind.Global, Name = "global", IdleGuardSources = "keyboard, macro, phone" };
        var m = new ProfileLayer { Kind = LayerKind.Mud, Name = "3s" };
        var c = new ProfileLayer { Kind = LayerKind.Character, Name = "Kuben", IdleGuardSources = "keyboard" };

        Assert.Equal(IdleSource.Keyboard, ProfileResolver.Resolve(new[] { g, m, c }).IdleGuardSources);
        Assert.Equal(IdleSource.Keyboard | IdleSource.Macro | IdleSource.Phone, ProfileResolver.Resolve(new[] { g, m }).IdleGuardSources);
        Assert.Equal(IdleSource.All, ProfileResolver.Resolve(new[] { m }).IdleGuardSources);
    }
}
