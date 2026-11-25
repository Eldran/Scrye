using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The <c>.profile</c> accounting on a clock the test moves by hand (1 tick = 1 ms): what a
/// span's own time is when spans nest, and what the report says.
/// </summary>
public sealed class PluginProfilerTests
{
    private long _t;
    private PluginProfiler New() => new(() => _t, ticksPerSecond: 1000);

    private static ProfileRow Row(PluginProfiler p, string what, string? detail = null)
    {
        foreach (ProfileRow r in p.Rows())
            if (r.What == what && r.Detail == detail) return r;
        throw new Xunit.Sdk.XunitException($"no row '{what} {detail}'");
    }

    [Fact]
    public void ASpanWithNothingInsideIsAllItsOwnTime()
    {
        PluginProfiler p = New();
        PluginProfiler.Slot s = p.For("a", ProfileKind.Handler, "onLine");
        int span = p.Enter(); _t += 4; p.Leave(span, s);
        span = p.Enter(); _t += 10; p.Leave(span, s);

        ProfileRow r = Row(p, "onLine");
        Assert.Equal(2, r.Calls);
        Assert.Equal(14, r.SelfMs);
        Assert.Equal(14, r.TotalMs);
        Assert.Equal(10, r.MaxMs);
    }

    [Fact]
    public void NestedSpansAreTakenOutOfTheirParentsOwnTime()
    {
        // handler (2 ms of script) -> scrye.emit (1 ms) -> another plugin's handler (5 ms)
        PluginProfiler p = New();
        int outer = p.Enter(); _t += 2;
        int api = p.Enter(); _t += 1;
        int inner = p.Enter(); _t += 5;
        p.Leave(inner, p.For("b", ProfileKind.Handler, "on:tick"));
        p.Leave(api, p.For("a", ProfileKind.Api, "emit"));
        p.Leave(outer, p.For("a", ProfileKind.Handler, "onGmcp", "Char.Vitals"));

        Assert.Equal((2d, 8d), (Row(p, "onGmcp", "Char.Vitals").SelfMs, Row(p, "onGmcp", "Char.Vitals").TotalMs));
        Assert.Equal((1d, 6d), (Row(p, "emit").SelfMs, Row(p, "emit").TotalMs));
        Assert.Equal((5d, 5d), (Row(p, "on:tick").SelfMs, Row(p, "on:tick").TotalMs));
        // own times add up to the wall time, nothing counted twice
        double sum = 0;
        foreach (ProfileRow r in p.Rows()) sum += r.SelfMs;
        Assert.Equal(8, sum);
    }

    [Fact]
    public void ALeafCountsAgainstTheSpanAroundIt()
    {
        PluginProfiler p = New();
        int span = p.Enter(); _t += 10;
        p.Leaf(p.For("a", ProfileKind.Match, "trigger", "^You kill"), 3);
        p.Leave(span, p.For("a", ProfileKind.Handler, "onLine"));

        Assert.Equal(7, Row(p, "onLine").SelfMs);
        Assert.Equal(3, Row(p, "trigger", "^You kill").SelfMs);
    }

    [Fact]
    public void RowsComeMostOwnTimeFirst()
    {
        PluginProfiler p = New();
        int s = p.Enter(); _t += 1; p.Leave(s, p.For("a", ProfileKind.Handler, "small"));
        s = p.Enter(); _t += 9; p.Leave(s, p.For("b", ProfileKind.Handler, "big"));
        s = p.Enter(); _t += 3; p.Leave(s, p.For("a", ProfileKind.Api, "mid"));

        Assert.Equal(new[] { "big", "mid", "small" }, p.Rows().Select(r => r.What).ToArray());
    }

    [Fact]
    public void OffMeasuresNothingAndKeepsWhatWasGathered()
    {
        PluginProfiler p = New();
        PluginProfiler.Slot s = p.For("a", ProfileKind.Handler, "onLine");
        int span = p.Enter(); _t += 5; p.Leave(span, s);

        p.Enabled = false;
        span = p.Enter();
        Assert.Equal(-1, span);
        _t += 50;
        p.Leave(span, s);
        p.Leaf(s, 50);

        Assert.Equal(1, Row(p, "onLine").Calls);
        Assert.Equal(5, Row(p, "onLine").TotalMs);
    }

    [Fact]
    public void TurningItOffInsideASpanStillClosesThatSpan()
    {
        PluginProfiler p = New();
        PluginProfiler.Slot s = p.For("a", ProfileKind.Handler, "onLine");
        int span = p.Enter(); _t += 5;
        p.Enabled = false;
        p.Leave(span, s);
        p.Enabled = true;
        // the stack is empty again: the next span is nobody's child
        span = p.Enter(); _t += 2; p.Leave(span, s);
        Assert.Equal(7, Row(p, "onLine").SelfMs);
    }

    [Fact]
    public void ASpanLeftOpenByAnExceptionIsDroppedWhenItsParentCloses()
    {
        PluginProfiler p = New();
        PluginProfiler.Slot s = p.For("a", ProfileKind.Handler, "onLine");
        int outer = p.Enter(); _t += 1;
        p.Enter(); _t += 1;            // never left
        p.Leave(outer, s);

        int next = p.Enter();
        Assert.Equal(0, next);         // back at the bottom of the stack
        _t += 3; p.Leave(next, s);
        Assert.Equal(2, Row(p, "onLine").Calls);
        Assert.Equal(5, Row(p, "onLine").TotalMs);
    }

    [Fact]
    public void ResetZeroesInPlaceSoAHeldSlotKeepsWorking()
    {
        PluginProfiler p = New();
        PluginProfiler.Slot a = p.For("a", ProfileKind.Handler, "onLine");
        PluginProfiler.Slot b = p.For("b", ProfileKind.Handler, "onLine");
        int span = p.Enter(); _t += 5; p.Leave(span, a);
        span = p.Enter(); _t += 5; p.Leave(span, b);

        p.Reset("a");
        Assert.Equal(new[] { "b" }, p.Rows().Select(r => r.PluginId).ToArray());

        span = p.Enter(); _t += 2; p.Leave(span, a);   // the slot a reloaded-or-not runtime holds
        Assert.Equal(2, p.Rows().Count);

        _t += 100;
        p.Reset();
        Assert.Empty(p.Rows());
        Assert.Equal(0, p.WindowMs);
        span = p.Enter(); _t += 4; p.Leave(span, b);
        Assert.Equal(4, Row(p, "onLine").TotalMs);
        Assert.Same(a, p.For("a", ProfileKind.Handler, "onLine"));
    }

    [Fact]
    public void ALongPatternIsCutInTheRowNameAndStillOneRow()
    {
        PluginProfiler p = New();
        string pattern = new string('x', 200);
        PluginProfiler.Slot s = p.For("a", ProfileKind.Match, "trigger", pattern);
        Assert.Same(s, p.For("a", ProfileKind.Match, "trigger", pattern));
        p.Leaf(s, 1);
        Assert.Equal(PluginProfiler.MaxDetailLength, p.Rows()[0].Detail!.Length);
    }

    [Fact]
    public void TheReportSumsPerPluginAndListsTheCostliestRows()
    {
        PluginProfiler p = New();
        int h = p.Enter(); _t += 20;
        int a = p.Enter(); _t += 70; p.Leave(a, p.For("3s-map", ProfileKind.Api, "addPanel"));
        p.Leave(h, p.For("3s-map", ProfileKind.Handler, "onGmcp", "Room.Info"));
        p.Leaf(p.For("3s-chat", ProfileKind.Match, "trigger", "^(\\w+) tells you"), 10);
        _t += 900;   // the window: 1000 ms, 100 of them in plugins (the leaf's 10 were not on the clock)

        string[] lines = p.Report().ToArray();
        Assert.Equal("plugin profile: 100 ms in plugins over 0s = 10.1% of the time", lines[0]);
        Assert.Contains("script", lines[1]);
        Assert.Matches(@"^  3s-map\s+90\.0 ms\s+20\.0 ms\s+70\.0 ms\s+0\.0 µs$", lines[2]);
        Assert.Matches(@"^  3s-chat\s+10\.0 ms\s+0\.0 µs\s+0\.0 µs\s+10\.0 ms$", lines[3]);
        Assert.Equal("  by own time:", lines[4]);
        Assert.EndsWith("3s-map  scrye.addPanel", lines[6]);
        Assert.Matches(@"^\s+20\.0 ms\s+90\.0 ms\s+1\s+90\.0 ms\s+90\.0 ms  3s-map  onGmcp Room\.Info$", lines[7]);
        Assert.EndsWith(@"3s-chat  match trigger ^(\w+) tells you", lines[8]);
        Assert.Equal(9, lines.Length);
    }

    [Fact]
    public void TheReportCanBeCutAndFiltered()
    {
        PluginProfiler p = New();
        for (int i = 1; i <= 5; i++) p.Leaf(p.For("a", ProfileKind.Api, "call" + i), i);
        p.Leaf(p.For("b", ProfileKind.Api, "store.set"), 2);

        string[] top2 = p.Report(top: 2).ToArray();
        Assert.Contains("  top 2 of 6 by own time:", top2);
        Assert.EndsWith("a  scrye.call5", top2[^2]);
        Assert.EndsWith("a  scrye.call4", top2[^1]);

        string[] store = p.Report(filter: "STORE").ToArray();
        Assert.StartsWith("plugin profile matching 'STORE': 2.0 ms in plugins", store[0]);
        Assert.EndsWith("b  scrye.store.set", store[^1]);
        Assert.DoesNotContain(store, l => l.Contains("call5"));

        Assert.StartsWith("plugin profile: nothing matches 'nope'", p.Report(filter: "nope")[0]);
        Assert.StartsWith("plugin profile: nothing measured", New().Report()[0]);
    }

    [Fact]
    public void TheReportSaysWhenProfilingIsOff()
    {
        PluginProfiler p = New();
        p.Leaf(p.For("a", ProfileKind.Api, "send"), 1);
        p.Enabled = false;
        Assert.Contains("profiling is OFF", p.Report()[0]);
    }

    [Theory]
    [InlineData("", PluginProfiler.DefaultTop, null)]
    [InlineData("30", 30, null)]
    [InlineData("all", int.MaxValue, null)]
    [InlineData("3s-map", PluginProfiler.DefaultTop, "3s-map")]
    [InlineData("3s-map 30", 30, "3s-map")]
    [InlineData("  onGmcp   Char.Vitals ", PluginProfiler.DefaultTop, "onGmcp Char.Vitals")]
    [InlineData("0", PluginProfiler.DefaultTop, "0")]
    public void ReportArgumentsAreANumberAllOrFilterText(string arg, int top, string? filter)
    {
        PluginProfiler.ParseReportArgs(arg, out int gotTop, out string? gotFilter);
        Assert.Equal(top, gotTop);
        Assert.Equal(filter, gotFilter);
    }

    [Fact]
    public void ReloadingAPluginClearsItsRows()
    {
        var d = new PluginDiagnostics(_ => { });
        d.Profiler.Leaf(d.Profiler.For("a", ProfileKind.Api, "send"), 5);
        d.Profiler.Leaf(d.Profiler.For("b", ProfileKind.Api, "send"), 5);
        d.Reset("a");
        Assert.Equal(new[] { "b" }, d.Profiler.Rows().Select(r => r.PluginId).ToArray());
    }
}
