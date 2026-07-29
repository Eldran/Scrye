using System.IO;
using Scrye.Core.Automation;
using Scrye.Core.Profiles;
using Xunit;

namespace Scrye.Core.Tests;

public class ProfileTests
{
    private static (ProfileLayer g, ProfileLayer m, ProfileLayer c) Sample()
    {
        var g = new ProfileLayer { Kind = LayerKind.Global, Name = "global", FontFamily = "Cascadia Mono", Theme = "Dark",
            Triggers = { new TriggerDef { Name = "clock", Send = "time" } } };
        var m = new ProfileLayer { Kind = LayerKind.Mud, Name = "3Scapes", Host = "3k.org", Port = 3200, EnableMip = true,
            Triggers = { new TriggerDef { Name = "welcome", Send = "look" } },
            Aliases = { new AliasDef { Name = "gd", Send = "get all from corpse" } } };
        var c = new ProfileLayer { Kind = LayerKind.Character, Name = "Warrior",
            Triggers = { new TriggerDef { Name = "welcome", Send = "wield sword" }, new TriggerDef { Name = "flee", Send = "flee" } },
            Suppress = { "clock" }, Variables = { ["class"] = "viking" } };
        return (g, m, c);
    }

    [Fact]
    public void ScalarsTakeDeepestSetValue()
    {
        var (g, m, c) = Sample();
        var eff = ProfileResolver.Resolve(new[] { g, m, c });
        Assert.Equal("3k.org", eff.World.Host);   // from MUD
        Assert.Equal(3200, eff.World.Port);
        Assert.True(eff.World.EnableMip);
        Assert.Equal("Cascadia Mono", eff.FontFamily);  // fell through from Global
        Assert.Equal("Dark", eff.Theme);
        Assert.Equal("Warrior", eff.World.Name);   // deepest layer name
    }

    [Fact]
    public void DeeperLayerOverridesSameNamedTrigger()
    {
        var (g, m, c) = Sample();
        var eff = ProfileResolver.Resolve(new[] { g, m, c });
        TriggerDef welcome = Assert.Single(eff.Triggers, t => t.Name == "welcome");
        Assert.Equal("wield sword", welcome.Send);   // character overrode the MUD's "look"
    }

    [Fact]
    public void SuppressDropsInheritedRule()
    {
        var (g, m, c) = Sample();
        var eff = ProfileResolver.Resolve(new[] { g, m, c });
        Assert.DoesNotContain(eff.Triggers, t => t.Name == "clock");   // suppressed by character
    }

    [Fact]
    public void CollectionsMergeAcrossLayers()
    {
        var (g, m, c) = Sample();
        var eff = ProfileResolver.Resolve(new[] { g, m, c });
        // welcome (overridden) + flee (added) = 2; clock suppressed
        Assert.Equal(2, eff.Triggers.Count);
        Assert.Contains(eff.Triggers, t => t.Name == "flee");
        Assert.Single(eff.Aliases, a => a.Name == "gd");
        Assert.Equal("viking", eff.Variables["class"]);
    }

    [Fact]
    public void LaterLayerScalarBeatsEarlier()
    {
        var mud = new ProfileLayer { Kind = LayerKind.Mud, Host = "old", Port = 1 };
        var chr = new ProfileLayer { Kind = LayerKind.Character, Port = 2 };   // overrides port, inherits host
        var eff = ProfileResolver.Resolve(new[] { mud, chr });
        Assert.Equal("old", eff.World.Host);
        Assert.Equal(2, eff.World.Port);
    }

    [Fact]
    public void JsonRoundTripsLayer()
    {
        var (_, _, c) = Sample();
        string json = ProfileStore.Serialize(c);
        ProfileLayer back = ProfileStore.Deserialize(json);
        Assert.Equal("Warrior", back.Name);
        Assert.Equal(LayerKind.Character, back.Kind);
        Assert.Equal(2, back.Triggers.Count);
        Assert.Contains("clock", back.Suppress);
        Assert.Equal("viking", back.Variables["class"]);
        Assert.Equal("wield sword", back.Triggers.Single(t => t.Name == "welcome").Send);
    }

    [Fact]
    public void UnsetScalarsAreOmittedFromJson()
    {
        var layer = new ProfileLayer { Kind = LayerKind.Mud, Name = "x", Host = "h" };
        string json = ProfileStore.Serialize(layer);
        Assert.DoesNotContain("port", json.ToLowerInvariant());  // Port was null -> omitted
        Assert.Contains("\"host\"", json);
    }

    [Fact]
    public void WorldStoreSavesListsResolvesDeletes()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrye_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(root);
            store.SaveGlobal(new ProfileLayer { Kind = LayerKind.Global, Theme = "Dark" });
            store.SaveWorld("3Scapes", new ProfileLayer { Host = "3k.org", Port = 3200, EnableMip = true });
            store.SaveWorld("Aardwolf", new ProfileLayer { Host = "aardmud.org", Port = 4000 });

            Assert.Equal(new[] { "3Scapes", "Aardwolf" }, store.ListWorlds());

            var eff = new ProfileStore(root).ResolveWorld("3Scapes");   // fresh instance = from disk
            Assert.Equal("3k.org", eff.World.Host);
            Assert.Equal(3200, eff.World.Port);
            Assert.True(eff.World.EnableMip);
            Assert.Equal("Dark", eff.Theme);   // fell through from global

            store.DeleteWorld("Aardwolf");
            Assert.Equal(new[] { "3Scapes" }, store.ListWorlds());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
