using System;
using System.Collections.Generic;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Session;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// <see cref="SendTo.Client"/>: a rule, or a <c>&gt;</c>-prefixed sequence step, running its
/// text back through the client's own command pipeline instead of putting it on the wire.
///
/// <para>The point is plugin commands. <c>cs pause</c> means something to the chaos-sea plugin
/// and nothing at all to 3Scapes, so a trigger that wants to pause the bot, loot, and resume
/// cannot use <see cref="SendTo.World"/> -- the MUD would answer "Huh?" three times.</para>
///
/// <para>Two properties are load-bearing and pinned hard here: the ';' separator is applied to
/// the TEMPLATE the author wrote and never to text a wildcard carried in from the MUD, and the
/// nesting is bounded, because the pipeline can reach a rule that reaches the pipeline.</para>
/// </summary>
public class ClientDestinationTests
{
    /// <summary>A world that records which door each command went out of.</summary>
    private sealed class Spy : IWorldActions
    {
        public readonly List<string> Wire = new();
        public readonly List<string> Client = new();
        public void Send(string text) => Wire.Add(text);
        public void SendToClient(string text) => Client.Add(text);
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    /// <summary>A world whose pipeline is the engine itself, the way <c>MudSession</c>'s is.</summary>
    private sealed class Recur : IWorldActions
    {
        private readonly AutomationEngine _engine;
        public readonly List<string> Wire = new();
        public Recur(AutomationEngine engine) => _engine = engine;
        public void Send(string text) => Wire.Add(text);
        public void SendToClient(string text)
        {
            if (!_engine.ProcessInput(text, this)) Wire.Add(text);
        }
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    /// <summary>A world that does NOT override <c>SendToClient</c>.</summary>
    private sealed class Plain : IWorldActions
    {
        public readonly List<string> Wire = new();
        public void Send(string text) => Wire.Add(text);
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) { }
    }

    private static AutomationEngine Engine() => new(new VariableStore());

    private static AutomationEngine WithAlias(string pattern, string send,
                                              SendTo to = SendTo.Client) 
    {
        AutomationEngine e = Engine();
        e.AddAlias(new AliasDef { Name = "a", Pattern = pattern, IsRegex = true, SendTo = to, Send = send });
        return e;
    }

    // ---- the two destinations ----------------------------------------------

    [Fact]
    public void World_still_goes_straight_to_the_wire()
    {
        var w = new Spy();
        WithAlias("^csp$", "cs pause", SendTo.World).ProcessInput("csp", w);
        Assert.Equal(new[] { "cs pause" }, w.Wire);
        Assert.Empty(w.Client);
    }

    [Fact]
    public void Client_goes_through_the_pipeline_instead()
    {
        var w = new Spy();
        WithAlias("^csp$", "cs pause").ProcessInput("csp", w);
        Assert.Equal(new[] { "cs pause" }, w.Client);
        Assert.Empty(w.Wire);
    }

    [Fact]
    public void A_timer_can_drive_a_plugin_command()
    {
        AutomationEngine e = Engine();
        e.AddTimer(new TimerDef { Name = "t", IntervalSeconds = 1, SendTo = SendTo.Client, Send = "cs step" });
        var w = new Spy();
        e.Tick(1.0, w);
        Assert.Equal(new[] { "cs step" }, w.Client);
    }

    [Fact]
    public void A_host_without_a_pipeline_degrades_to_send()
    {
        // Better than dropping the command: a headless host that cannot run aliases can at
        // least still put the text on the wire.
        var w = new Plain();
        WithAlias("^d$", "cs pause").ProcessInput("d", w);
        Assert.Equal(new[] { "cs pause" }, w.Wire);
    }

    // ---- the separator -----------------------------------------------------

    [Fact]
    public void A_semicolon_in_the_send_box_makes_several_commands()
    {
        AutomationEngine e = Engine();
        e.AddTrigger(new TriggerDef { Name = "loot", Pattern = "^You have slain", IsRegex = true,
                                      SendTo = SendTo.Client, Send = "cs pause;get all;cs resume" });
        var w = new Spy();
        e.ProcessLine("You have slain the orc.", w);
        Assert.Equal(new[] { "cs pause", "get all", "cs resume" }, w.Client);
    }

    [Fact]
    public void A_semicolon_the_MUD_supplied_through_a_wildcard_is_not_a_separator()
    {
        // The split is applied to the template, before expansion, and this is why: the MUD
        // authored the text %1 carries, and the MUD does not get to fan one rule out into
        // several client commands -- each of which could match an alias that runs script.
        // Same reasoning as MudSession.SubmitLiteral keeping an MXP link away from ';'.
        AutomationEngine e = Engine();
        e.AddTrigger(new TriggerDef { Name = "say", Pattern = "^say (.*)$", IsRegex = true,
                                      SendTo = SendTo.Client, Send = "shout %1" });
        var w = new Spy();
        e.ProcessLine("say hello;quit", w);
        Assert.Equal(new[] { "shout hello;quit" }, w.Client);
    }

    [Fact]
    public void A_doubled_semicolon_is_still_a_literal_one()
    {
        var w = new Spy();
        WithAlias("^t$", "say one;; two").ProcessInput("t", w);
        Assert.Equal(new[] { "say one; two" }, w.Client);
    }

    [Fact]
    public void Newlines_and_semicolons_compose()
    {
        var w = new Spy();
        WithAlias("^m$", "cs pause\nget all;get gold\ncs resume").ProcessInput("m", w);
        Assert.Equal(new[] { "cs pause", "get all", "get gold", "cs resume" }, w.Client);
    }

    [Fact]
    public void An_empty_send_runs_nothing()
    {
        var w = new Spy();
        WithAlias("^e$", "").ProcessInput("e", w);
        Assert.Empty(w.Client);
        Assert.Empty(w.Wire);
    }

    [Fact]
    public void The_dry_run_says_run_rather_than_send()
    {
        AutomationEngine e = Engine();
        e.AddTrigger(new TriggerDef { Name = "sim", Pattern = "^ping$", IsRegex = true,
                                      SendTo = SendTo.Client, Send = "cs pause" });
        IReadOnlyList<AutomationHit> hits = e.Simulate("ping");
        Assert.Single(hits);
        Assert.Contains("run: cs pause", hits[0].Action);
    }

    // ---- re-entrancy -------------------------------------------------------

    [Fact]
    public void A_client_alias_can_reach_another_alias()
    {
        AutomationEngine e = Engine();
        e.AddAlias(new AliasDef { Name = "first", Pattern = "^one$", IsRegex = true,
                                  SendTo = SendTo.Client, Send = "two" });
        e.AddAlias(new AliasDef { Name = "second", Pattern = "^two$", IsRegex = true,
                                  SendTo = SendTo.World, Send = "sent-by-second" });
        var loop = new Recur(e);
        e.ProcessInput("one", loop);
        Assert.Equal(new[] { "sent-by-second" }, loop.Wire);
    }

    [Fact]
    public void A_nested_one_shot_removal_retires_the_alias_that_fired_and_no_other()
    {
        // The index bug this guards against: the nested pass removes the one-shot at index 0
        // while the outer loop holds index 1. Removing by POSITION afterwards would retire
        // index 1 of the shrunk list -- which is now the innocent alias after it.
        AutomationEngine e = Engine();
        e.AddAlias(new AliasDef { Name = "inner", Pattern = "^inner$", IsRegex = true, OneShot = true,
                                  SendTo = SendTo.World, Send = "x" });
        e.AddAlias(new AliasDef { Name = "outer", Pattern = "^go$", IsRegex = true, OneShot = true,
                                  SendTo = SendTo.Client, Send = "inner" });
        e.AddAlias(new AliasDef { Name = "bystander", Pattern = "^zz$", IsRegex = true,
                                  SendTo = SendTo.World, Send = "zed" });
        var loop = new Recur(e);
        e.ProcessInput("go", loop);

        var later = new List<string>();
        e.Hit += h => later.Add(h.Name);
        e.ProcessInput("go", loop);
        Assert.DoesNotContain("outer", later);      // the one that fired is the one retired
        e.ProcessInput("zz", loop);
        Assert.Contains("bystander", later);        // and the one after it survived
    }

    [Fact]
    public void Retiring_the_last_one_shot_after_a_nested_removal_does_not_go_out_of_range()
    {
        AutomationEngine e = Engine();
        e.AddAlias(new AliasDef { Name = "inner", Pattern = "^inner$", IsRegex = true, OneShot = true,
                                  SendTo = SendTo.World, Send = "x" });
        e.AddAlias(new AliasDef { Name = "outer", Pattern = "^go$", IsRegex = true, OneShot = true,
                                  SendTo = SendTo.Client, Send = "inner" });
        var loop = new Recur(e);
        e.ProcessInput("go", loop);   // by position this is RemoveAt(1) on a one-element list
        Assert.Equal(new[] { "x" }, loop.Wire);
    }

    // ---- the loop guard, on a real session ---------------------------------

    private static MudSession Session() =>
        new(new WorldProfile { Host = "localhost", Port = 1 });

    [Fact]
    public void A_self_feeding_alias_stops_at_the_depth_cap()
    {
        MudSession session = Session();
        var lines = new List<string>();
        session.LineReady += l => lines.Add(l.PlainText);
        int fires = 0;
        session.Automation.Hit += _ => fires++;
        session.Automation.AddAlias(new AliasDef { Name = "loop", Pattern = "^loop$", IsRegex = true,
                                                   SendTo = SendTo.Client, Send = "loop" });

        ((IWorldActions)session).SendToClient("loop");

        Assert.Equal(5, fires);
        Assert.Contains(lines, l => l.Contains("command loop stopped"));
        Assert.Contains(lines, l => l.Contains("'loop' was not run"));
    }

    [Fact]
    public void The_depth_counter_unwinds()
    {
        MudSession session = Session();
        var lines = new List<string>();
        session.LineReady += l => lines.Add(l.PlainText);
        session.Automation.AddAlias(new AliasDef { Name = "loop", Pattern = "^loop$", IsRegex = true,
                                                   SendTo = SendTo.Client, Send = "loop" });
        session.Automation.AddAlias(new AliasDef { Name = "ok", Pattern = "^fine$", IsRegex = true,
                                                   SendTo = SendTo.Client, Send = "cs pause" });
        ((IWorldActions)session).SendToClient("loop");

        lines.Clear();
        int fires = 0;
        session.Automation.Hit += _ => fires++;
        ((IWorldActions)session).SendToClient("fine");

        Assert.Equal(1, fires);
        Assert.DoesNotContain(lines, l => l.Contains("command loop stopped"));
    }

    [Fact]
    public void A_rules_client_command_is_not_a_person_at_the_keyboard()
    {
        // The idle guard is poked by typing and by nothing else, which is what stops a bot
        // walking an area all night from reading as presence. Running the pipeline on a rule's
        // behalf must not change that, so this goes through RunInput, not HandleInput.
        MudSession session = Session();
        session.IdleGuard.Enabled = true;
        session.IdleGuard.Tick(30);
        double before = session.IdleGuard.IdleSeconds;

        ((IWorldActions)session).SendToClient("cs pause");

        Assert.Equal(before, session.IdleGuard.IdleSeconds, 6);
    }

    // ---- sequences ---------------------------------------------------------

    [Fact]
    public void A_prefixed_sequence_step_is_a_client_step()
    {
        SequenceDef d = SequenceParser.Parse("loot", ">cs pause; open cask; get all; wait 2; >cs resume");
        Assert.Equal(5, d.Steps.Count);
        Assert.Equal("client", d.Steps[0].Kind);
        Assert.Equal("cs pause", d.Steps[0].Text);
        Assert.Equal("send", d.Steps[1].Kind);
        Assert.Equal("wait", d.Steps[3].Kind);
        Assert.Equal("client", d.Steps[4].Kind);
    }

    [Fact]
    public void Every_step_of_a_speedwalk_written_before_this_is_untouched()
    {
        SequenceDef d = SequenceParser.Parse("walk", "n;n;e;s;n x4");
        Assert.All(d.Steps, s => Assert.Equal("send", s.Kind));
    }

    [Theory]
    [InlineData(">>look", "send", ">look")]      // ">>" is a literal ">"
    [InlineData(">  cs pause", "client", "cs pause")]   // space after ">" is trimmed
    [InlineData(">wait 2", "client", "wait 2")]  // prefixed, so a command and not a delay
    public void Prefix_parsing(string source, string kind, string text)
    {
        SequenceDef d = SequenceParser.Parse("s", source);
        Assert.Single(d.Steps);
        Assert.Equal(kind, d.Steps[0].Kind);
        Assert.Equal(text, d.Steps[0].Text);
    }

    [Fact]
    public void A_client_step_takes_a_repeat_count()
    {
        SequenceDef d = SequenceParser.Parse("rep", ">cs step x3");
        Assert.Single(d.Steps);
        Assert.Equal("client", d.Steps[0].Kind);
        Assert.Equal(3, d.Steps[0].Count);
    }

    [Fact]
    public void A_bare_arrow_asks_for_no_command()
    {
        SequenceDef d = SequenceParser.Parse("bare", "n; > ; s");
        Assert.Equal(2, d.Steps.Count);
    }

    [Fact]
    public void The_runner_routes_client_steps_to_the_pipeline()
    {
        SequenceDef d = SequenceParser.Parse("loot", ">cs pause; get all; >cs resume", promptGated: false)
                        with { StepDelaySeconds = 0 };
        var engine = new SequenceEngine();
        var wire = new List<string>();
        var client = new List<string>();
        engine.Send += wire.Add;
        engine.SendClient += client.Add;
        engine.Register(d);
        engine.Run("loot");
        for (int i = 0; i < 10 && engine.State != SequenceState.Finished; i++) engine.Tick(0.1);

        Assert.Equal(new[] { "get all" }, wire);
        Assert.Equal(new[] { "cs pause", "cs resume" }, client);
    }

    [Fact]
    public void With_no_pipeline_subscribed_a_client_step_still_runs()
    {
        SequenceDef d = SequenceParser.Parse("loot", ">cs pause; get all", promptGated: false)
                        with { StepDelaySeconds = 0 };
        var engine = new SequenceEngine();
        var wire = new List<string>();
        engine.Send += wire.Add;
        engine.Register(d);
        engine.Run("loot");
        for (int i = 0; i < 10 && engine.State != SequenceState.Finished; i++) engine.Tick(0.1);

        Assert.Equal(new[] { "cs pause", "get all" }, wire);
    }

    [Fact]
    public void The_progress_strip_counts_client_steps_too()
    {
        SequenceDef d = SequenceParser.Parse("loot", ">cs pause; get all; wait 1; >cs resume",
                                             promptGated: false) with { StepDelaySeconds = 0 };
        var engine = new SequenceEngine();
        var seen = new List<SequenceStatus>();
        engine.Send += _ => { };
        engine.SendClient += _ => { };
        engine.StatusChanged += seen.Add;
        engine.Register(d);
        engine.Run("loot");

        Assert.NotEmpty(seen);
        Assert.Equal(3, seen[0].Total);
    }

    // ---- the MUSHclient importer -------------------------------------------

    [Fact]
    public void Mushclient_send_to_10_now_imports_as_the_client_destination()
    {
        // "Send to Execute" -- MUSHclient's own re-parse. It used to be skipped for want of a
        // Scrye equivalent; now there is one.
        const string Xml = """
        <muclient><plugin name="p"><triggers>
          <trigger enabled="y" match="^You are hungry$" regexp="y" send_to="10" sequence="100" name="hungry">
            <send>eat bread</send>
          </trigger>
        </triggers></plugin></muclient>
        """;
        MushclientImport r = MushclientImport.Parse(Xml, "g");
        Assert.Single(r.Triggers);
        Assert.Equal(SendTo.Client, r.Triggers[0].SendTo);
        Assert.Empty(r.Skipped);
    }
}
