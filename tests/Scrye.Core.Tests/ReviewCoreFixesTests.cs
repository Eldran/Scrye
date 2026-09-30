using System.IO.Compression;
using Scrye.Core.Automation;
using Scrye.Core.Gmcp;
using Scrye.Core.Plugins;
using Scrye.Core.Profiles;
using Scrye.Core.State;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>Regression tests for review findings in Core: rule ordering and one-shot removal,
/// sequence repeat parsing, state watchers, plugin store sharing, zip-slip, profile names/IO,
/// GMCP audit memory.</summary>
public sealed class ReviewCoreFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrye-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Recorder : IWorldActions
    {
        public List<string> Sends { get; } = new();
        public Action<string>? OnScript;
        public void Send(string text) => Sends.Add(text);
        public void Echo(string text) { }
        public string? GetVariable(string name) => null;
        public void SetVariable(string name, string value) { }
        public void CallScript(string function, IReadOnlyList<string> wildcards) => OnScript?.Invoke(function);
    }

    // ---- AutomationEngine ------------------------------------------------------

    [Fact]
    public void EqualSequenceTriggersKeepInsertionOrderPastSixteen()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        for (int i = 0; i < 40; i++)
            engine.AddTrigger(new TriggerDef { Name = "t" + i, Pattern = "hit", Send = "t" + i, KeepEvaluating = true });

        engine.ProcessLine("hit", rec);

        Assert.Equal(Enumerable.Range(0, 40).Select(i => "t" + i).ToArray(), rec.Sends.ToArray());
    }

    [Fact]
    public void FirstAddedOfEqualSequenceWinsWithoutKeepEvaluating()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        for (int i = 0; i < 40; i++)
        {
            engine.AddTrigger(new TriggerDef { Name = "t" + i, Pattern = "hit", Send = "t" + i });
            engine.AddAlias(new AliasDef { Name = "a" + i, Pattern = "go", Send = "a" + i });
        }
        engine.ProcessLine("hit", rec);
        engine.ProcessInput("go", rec);
        Assert.Equal(new[] { "t0", "a0" }, rec.Sends.ToArray());
    }

    [Fact]
    public void OneShotTriggerWhoseScriptAddsEarlierRuleRemovesItselfNotAnother()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        engine.AddTrigger(new TriggerDef { Name = "a", Pattern = "hit", Send = "a", KeepEvaluating = true, Sequence = 10 });
        engine.AddTrigger(new TriggerDef { Name = "once", Pattern = "hit", Send = "once", Script = "add",
                                           OneShot = true, KeepEvaluating = true, Sequence = 20 });
        engine.AddTrigger(new TriggerDef { Name = "c", Pattern = "hit", Send = "c", KeepEvaluating = true, Sequence = 30 });
        // the one-shot's script adds a rule that sorts BEFORE it: the old RemoveAt(i) then
        // removed that new rule's neighbour (or the new rule) instead of the one-shot
        rec.OnScript = fn => engine.AddTrigger(new TriggerDef { Name = "early", Pattern = "zzz", Sequence = 1 });

        engine.ProcessLine("hit", rec);
        Assert.Equal(new[] { "a", "once", "c" }, rec.Sends.ToArray());   // c neither skipped nor doubled

        rec.Sends.Clear();
        rec.OnScript = null;
        engine.ProcessLine("hit", rec);
        Assert.Equal(new[] { "a", "c" }, rec.Sends.ToArray());           // the one-shot, and only it, is gone
        Assert.Equal(3, engine.TriggerCount);                            // a, c, early
    }

    [Fact]
    public void TriggerRemovedByEarlierScriptDoesNotFireThisLine()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        engine.AddTrigger(new TriggerDef { Name = "a", Pattern = "hit", Send = "a", Script = "rm", KeepEvaluating = true, Sequence = 10 });
        engine.AddTrigger(new TriggerDef { Name = "b", Pattern = "hit", Send = "b", KeepEvaluating = true, Sequence = 20 });
        rec.OnScript = _ => engine.RemoveTrigger("b");
        engine.ProcessLine("hit", rec);
        Assert.Equal(new[] { "a" }, rec.Sends.ToArray());
    }

    [Fact]
    public void OneShotTimerWhoseScriptRemovesAnotherTimerRemovesTheRightOne()
    {
        var engine = new AutomationEngine(new VariableStore());
        var rec = new Recorder();
        engine.AddTimer(new TimerDef { Name = "x", IntervalSeconds = 1, Send = "x" });
        engine.AddTimer(new TimerDef { Name = "once", IntervalSeconds = 1, Send = "once", Script = "rm", OneShot = true });
        engine.AddTimer(new TimerDef { Name = "y", IntervalSeconds = 1, Send = "y" });
        rec.OnScript = _ => engine.RemoveTimer("x");

        engine.Tick(1, rec);
        Assert.Equal(new[] { "x", "once", "y" }, rec.Sends.ToArray());   // y not skipped
        Assert.Equal(1, engine.TimerCount);                              // only y left
        rec.Sends.Clear();
        engine.Tick(1, rec);
        Assert.Equal(new[] { "y" }, rec.Sends.ToArray());
    }

    [Fact]
    public void HitSummaryStillProducedWhenListening()
    {
        var engine = new AutomationEngine(new VariableStore());
        var hits = new List<AutomationHit>();
        engine.AddTrigger(new TriggerDef { Name = "t", Pattern = "hit *", Send = "kill %1" });
        engine.ProcessLine("hit orc", new Recorder());          // nobody listening: must not throw
        engine.Hit = hits.Add;
        engine.ProcessLine("hit orc", new Recorder());
        Assert.Single(hits);
        Assert.Contains("kill orc", hits[0].ToString());
    }

    // ---- Sequences -------------------------------------------------------------

    [Theory]
    [InlineData("sell box 2")]
    [InlineData("tax 5")]
    [InlineData("wax 3")]
    [InlineData("north")]
    public void WordsEndingInXAreNotRepeats(string step)
    {
        SequenceDef d = SequenceParser.Parse("t", step);
        Assert.Single(d.Steps);
        Assert.Equal(step, d.Steps[0].Text);
        Assert.Equal(1, d.Steps[0].Count);
    }

    [Theory]
    [InlineData("north x3", "north", 3)]
    [InlineData("north x 3", "north", 3)]
    [InlineData("north X3", "north", 3)]
    [InlineData("north*3", "north", 3)]
    [InlineData("north * 3", "north", 3)]
    [InlineData("sell box x2", "sell box", 2)]
    public void RepeatSyntaxStillParses(string step, string body, int count)
    {
        SequenceDef d = SequenceParser.Parse("t", step);
        Assert.Single(d.Steps);
        Assert.Equal(body, d.Steps[0].Text);
        Assert.Equal(count, d.Steps[0].Count);
    }

    [Fact]
    public void HugeRepeatIsCappedInsteadOfThrowing()
    {
        SequenceDef d = SequenceParser.Parse("t", "n x99999999999999999999; s x5000");
        Assert.Equal(SequenceParser.MaxRepeat, d.Steps[0].Count);
        Assert.Equal(SequenceParser.MaxRepeat, d.Steps[1].Count);
    }

    // ---- StateStore --------------------------------------------------------------

    [Fact]
    public void WatcherDisposingItselfDoesNotSkipTheNext()
    {
        var store = new StateStore();
        var calls = new List<string>();
        IDisposable? first = null;
        first = store.Watch("char", (_, _) => { calls.Add("first"); first!.Dispose(); });
        store.Watch("char", (_, _) => calls.Add("second"));

        store.Set("char.hp", StateValue.Num(10));
        Assert.Equal(new[] { "first", "second" }, calls.ToArray());

        calls.Clear();
        store.Set("char.hp", StateValue.Num(11));
        Assert.Equal(new[] { "second" }, calls.ToArray());
    }

    [Fact]
    public void WatcherDisposedByAnEarlierCallbackIsNotCalled()
    {
        var store = new StateStore();
        var calls = new List<string>();
        IDisposable? second = null;
        store.Watch("char", (_, _) => { calls.Add("first"); second!.Dispose(); });
        second = store.Watch("char", (_, _) => calls.Add("second"));
        store.Set("char.hp", StateValue.Num(1));
        Assert.Equal(new[] { "first" }, calls.ToArray());
    }

    [Fact]
    public void WatchPrefixMatchingUnchanged()
    {
        var store = new StateStore();
        var seen = new List<string>();
        store.Watch("char.hp", (k, _) => seen.Add(k));
        store.Watch("", (k, _) => seen.Add("all:" + k));
        store.Set("char.hp", StateValue.Num(1));
        store.Set("char.hpmax", StateValue.Num(2));     // not under "char.hp."
        store.Set("char.hp.x", StateValue.Num(3));
        Assert.Equal(new[] { "char.hp", "all:char.hp", "all:char.hpmax", "char.hp.x", "all:char.hp.x" }, seen.ToArray());
    }

    // ---- PluginDataStore ----------------------------------------------------------

    [Fact]
    public void TwoStoresOnOneFileDoNotOverwriteEachOther()
    {
        // two characters on one MUD: each world builds its own store over the same shared file
        var a = new PluginDataStore(_dir, "3scapes.org");
        var b = new PluginDataStore(_dir, "3scapes.org");
        a.Get("mapper", "warm-up");                       // both caches loaded before either writes
        b.Get("mapper", "warm-up");
        a.Set("mapper", "room.1", "temple");
        b.Set("mapper", "room.2", "market");
        a.SetMany("mapper", new Dictionary<string, string> { ["room.3"] = "gate" });

        Assert.Equal("market", a.Get("mapper", "room.2"));
        Assert.Equal("temple", b.Get("mapper", "room.1"));

        var fresh = new PluginDataStore(_dir, "3scapes.org");
        Assert.Equal(3, fresh.Keys("mapper").Length);     // nothing lost on disk
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "3scapes.org"), "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentWritersFromTwoThreadsLoseNothing()
    {
        var a = new PluginDataStore(_dir, "host");
        var b = new PluginDataStore(_dir, "host");
        var errors = new List<string>();
        Task ta = Task.Run(() => { for (int i = 0; i < 200; i++) a.Set("mapper", "a" + i, "x"); });
        Task tb = Task.Run(() => { for (int i = 0; i < 200; i++) b.Set("mapper", "b" + i, "y"); });
        await Task.WhenAll(ta, tb);
        Assert.Equal(400, new PluginDataStore(_dir, "host", errors.Add).Keys("mapper").Length);
        Assert.Empty(errors);
    }

    [Fact]
    public void ExternalEditIsSeenByANewInstance()
    {
        var a = new PluginDataStore(_dir, "w");
        a.Set("p", "k", "v1");
        string file = Directory.GetFiles(Path.Combine(_dir, "w"))[0];
        File.WriteAllText(file, "{\"k\":\"edited-outside\"}");
        Assert.Equal("edited-outside", new PluginDataStore(_dir, "w").Get("p", "k"));
    }

    // ---- PluginPackage --------------------------------------------------------------

    [Fact]
    public void ZipSlipIntoSiblingWithSharedPrefixIsRejected()
    {
        Directory.CreateDirectory(_dir);
        string pkg = Path.Combine(_dir, "foo" + PluginPackage.Extension);
        using (ZipArchive zip = ZipFile.Open(pkg, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                w.Write("{\"id\":\"foo\",\"name\":\"Foo\",\"version\":\"1.0.0\"}");
            using (var w = new StreamWriter(zip.CreateEntry("../foobar/evil.txt").Open()))
                w.Write("pwned");
            using (var w = new StreamWriter(zip.CreateEntry("ok.txt").Open()))
                w.Write("fine");
        }
        string root = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(root);

        Assert.Equal("foo", PluginPackage.Install(pkg, root));
        Assert.True(File.Exists(Path.Combine(root, "foo", "ok.txt")));
        Assert.False(File.Exists(Path.Combine(root, "foobar", "evil.txt")));
    }

    // ---- ProfileStore -----------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/abs")]
    [InlineData("nul\0")]
    public void BadProfileNamesAreRejected(string bad)
    {
        var store = new ProfileStore(_dir);
        store.SaveMud("M", new ProfileLayer());
        store.SaveCharacter("M", null, "hero", new ProfileLayer());

        Assert.False(ProfileStore.IsValidName(bad, out string? reason));
        Assert.NotNull(reason);
        Assert.Throws<ArgumentException>(() => store.DeleteCharacter("M", null, bad));
        Assert.Throws<ArgumentException>(() => store.SaveCharacter("M", null, bad, new ProfileLayer()));
        Assert.Throws<ArgumentException>(() => store.DeleteMud(bad));
        Assert.Throws<ArgumentException>(() => store.RenameCharacter("M", null, "hero", bad));

        // the MUD and its character survived every attempt
        Assert.NotNull(store.LoadMud("M"));
        Assert.NotNull(store.LoadCharacter("M", null, "hero"));
    }

    [Fact]
    public void OrdinaryNamesAreAccepted()
    {
        Assert.True(ProfileStore.IsValidName("3Scapes", out _));
        Assert.True(ProfileStore.IsValidName("Goran the Brave", out _));
        Assert.True(ProfileStore.IsValidName("a.b", out _));
    }

    [Fact]
    public void CorruptProfileFailsWithClearError()
    {
        var store = new ProfileStore(_dir);
        store.SaveMud("M", new ProfileLayer { Host = "h" });
        File.WriteAllText(Path.Combine(_dir, "M", "mud.json"), "{ truncated");
        var ex = Assert.Throws<InvalidDataException>(() => store.LoadMud("M"));
        Assert.Contains("mud.json", ex.Message);
    }

    [Fact]
    public void SaveFileReplacesAtomicallyAndLeavesNoTemp()
    {
        var store = new ProfileStore(_dir);
        store.SaveMud("M", new ProfileLayer { Host = "one" });
        store.SaveMud("M", new ProfileLayer { Host = "two" });
        Assert.Equal("two", store.LoadMud("M")!.Host);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "M"), "*.tmp"));
    }

    // ---- GmcpAudit ------------------------------------------------------------------

    [Fact]
    public void DistinctCountStillCountsDifferentPayloads()
    {
        var audit = new GmcpAudit();
        audit.Observe("Char.Vitals", "{\"hp\":1}");
        audit.Observe("Char.Vitals", "{\"hp\":2}");
        audit.Observe("Char.Vitals", "{\"hp\":1}");
        Assert.Equal(2, audit.DistinctCount("Char.Vitals"));
        Assert.Equal(2, audit.Distinct("Char.Vitals").Count);
    }

    [Fact]
    public void DistinctTrackingIsBounded()
    {
        var audit = new GmcpAudit { Negotiated = true };
        for (int i = 0; i < GmcpAudit.MaxDistinctTracked + 500; i++)
            audit.Observe("Char.Vitals", "{\"hp\":" + i + "}");
        Assert.Equal(GmcpAudit.MaxDistinctTracked, audit.DistinctCount("Char.Vitals"));
        Assert.Contains(audit.Report(), l => l.Contains(GmcpAudit.MaxDistinctTracked + "+ distinct"));
    }
}
