using Scrye.Core.State;
using Xunit;

namespace Scrye.Core.Tests;

public class StateValueTests
{
    [Fact]
    public void TypedAccessorsNeverThrow()
    {
        Assert.Equal(42, StateValue.Num(42).AsNumber());
        Assert.Equal(0, StateValue.Str("not a number").AsNumber());
        Assert.Equal(7, StateValue.Str("nope").AsNumber(7));
        Assert.True(StateValue.Boolean(true).AsBool());
        Assert.True(StateValue.Str("1").AsBool());
        Assert.False(StateValue.Str("").AsBool());
        Assert.True(StateValue.Null.IsNull);
    }

    [Fact]
    public void NumbersAreInvariantCulture()
    {
        Assert.Equal("1.5", StateValue.Num(1.5).Text);
    }
}

public class StateStoreTests
{
    [Fact]
    public void SetJsonFlattensWithTypes()
    {
        var s = new StateStore();
        s.SetJson("Char.Vitals", "{\"hp\":42,\"name\":\"Bob\",\"fighting\":true}");
        Assert.Equal(StateKind.Number, s.Get("char.vitals.hp").Kind);
        Assert.Equal(42, s.Get("char.vitals.hp").AsNumber());
        Assert.Equal(StateKind.String, s.Get("char.vitals.name").Kind);
        Assert.Equal("Bob", s.Get("char.vitals.name").Text);
        Assert.Equal(StateKind.Bool, s.Get("char.vitals.fighting").Kind);
        Assert.True(s.Get("char.vitals.fighting").AsBool());
    }

    [Fact]
    public void NestedObjectsAndArraysFlatten()
    {
        var s = new StateStore();
        s.SetJson("Room", "{\"name\":\"Plaza\",\"exits\":[\"north\",\"south\"]}");
        Assert.Equal("Plaza", s.Get("room.name").Text);
        Assert.Equal("north", s.Get("room.exits.0").Text);
        Assert.Equal("south", s.Get("room.exits.1").Text);
    }

    [Fact]
    public void LeafWatchFiresOnlyForItsPath()
    {
        var s = new StateStore();
        int hpFires = 0;
        using var w = s.Watch("char.hp", (_, _) => hpFires++);
        s.Set("char.hp", StateValue.Num(10));
        s.Set("char.sp", StateValue.Num(5));   // different leaf — must not fire
        Assert.Equal(1, hpFires);
    }

    [Fact]
    public void SubtreeWatchFiresForDescendants()
    {
        var s = new StateStore();
        var seen = new List<string>();
        using var w = s.Watch("char", (p, _) => seen.Add(p));
        s.Set("char.hp", StateValue.Num(10));
        s.Set("char.vitals.sp", StateValue.Num(5));
        s.Set("room.name", StateValue.Str("Plaza"));   // outside subtree — must not fire
        Assert.Equal(new[] { "char.hp", "char.vitals.sp" }, seen);
    }

    [Fact]
    public void UnchangedValueFiresNothing()
    {
        var s = new StateStore();
        int fires = 0;
        s.Set("char.hp", StateValue.Num(10));
        using var w = s.Watch("char.hp", (_, _) => fires++);
        s.Set("char.hp", StateValue.Num(10));   // same value
        Assert.Equal(0, fires);
    }

    [Fact]
    public void ResendRemovesVanishedKeysButKeepsUnchangedSilent()
    {
        var s = new StateStore();
        s.SetJson("Char.Vitals", "{\"hp\":42,\"maxhp\":100,\"sp\":10}");

        var changed = new List<string>();
        using var w = s.Watch("char.vitals", (p, v) => changed.Add($"{p}={(v.IsNull ? "<removed>" : v.Text)}"));

        // resend without sp, hp changed, maxhp unchanged
        s.SetJson("Char.Vitals", "{\"hp\":50,\"maxhp\":100}");

        Assert.False(s.Has("char.vitals.sp"));                 // vanished
        Assert.Equal(50, s.Get("char.vitals.hp").AsNumber());  // updated
        Assert.Contains("char.vitals.sp=<removed>", changed);
        Assert.Contains("char.vitals.hp=50", changed);
        Assert.DoesNotContain(changed, c => c.StartsWith("char.vitals.maxhp")); // unchanged → silent
    }

    [Fact]
    public void DisposeStopsWatching()
    {
        var s = new StateStore();
        int fires = 0;
        var w = s.Watch("char.hp", (_, _) => fires++);
        s.Set("char.hp", StateValue.Num(1));
        w.Dispose();
        s.Set("char.hp", StateValue.Num(2));
        Assert.Equal(1, fires);
    }

    [Fact]
    public void PathsAreCaseInsensitive()
    {
        var s = new StateStore();
        s.Set("Char.HP", StateValue.Num(10));
        Assert.Equal(10, s.Get("char.hp").AsNumber());
        Assert.True(s.Has("CHAR.HP"));
    }
}
