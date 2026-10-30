using Scrye.Core.Automation;
using Scrye.Core.Profiles;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// Who a rule is for (Joakim, 28 Oct 2026: "a trigger can be specific for just 1 char or
/// account wide or even for 1 or more char"): a rule's OnlyFor narrows it to the characters
/// it names, wherever it sits, and a rule can be moved to the layer that should own it.
/// </summary>
public sealed class RuleScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrye-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- the list ---------------------------------------------------------------------

    [Fact]
    public void OnlyForIsACommaListTrimmedDedupedAndCaseBlind()
    {
        Assert.Equal(new[] { "Eldran", "Nille" }, RuleScope.Names(" Eldran ,, Nille,Nille"));
        Assert.Equal(new[] { "Eldran", "NILLE" }, RuleScope.Names("Eldran, NILLE, nille, Eldran"));   // the first spelling stays
        Assert.Empty(RuleScope.Names(null));
        Assert.Empty(RuleScope.Names("  ,  , "));
        Assert.Equal("Eldran, Nille", RuleScope.Normalize("  Eldran,Nille , eldran "));
        Assert.Null(RuleScope.Normalize(" , "));
        Assert.Null(RuleScope.Normalize(null));
    }

    [Theory]
    [InlineData(null, "Eldran", true)]          // everyone's rule
    [InlineData("", null, true)]                // ...even with no character
    [InlineData("Eldran, Nille", "Nille", true)]
    [InlineData("Eldran, Nille", "nille", true)] // names are case-blind, as the MUD treats them
    [InlineData("Eldran, Nille", " Eldran ", true)]
    [InlineData("Eldran, Nille", "Goran", false)]
    [InlineData("Eldran", null, false)]          // a narrowed rule needs someone to match
    [InlineData("Eldran", "", false)]
    [InlineData("Eldran", "Eldra", false)]       // whole names, not prefixes
    public void AllowsMatchesWholeNames(string? onlyFor, string? character, bool expected) =>
        Assert.Equal(expected, RuleScope.Allows(onlyFor, character));

    // ---- the cascade ------------------------------------------------------------------

    private static ProfileLayer Account() => new()
    {
        Kind = LayerKind.Account, Name = "Joakim",
        Triggers = { new TriggerDef { Name = "all", Send = "x" },
                     new TriggerDef { Name = "two", Send = "y", OnlyFor = "Eldran, Nille" } },
        Aliases = { new AliasDef { Name = "gd", Send = "get all", OnlyFor = "Nille" } },
        Timers = { new TimerDef { Name = "tick", Send = "z", OnlyFor = "Goran" } },
        Sequences = { new SequenceSpec { Name = "walk", Source = "n", OnlyFor = "Eldran" } },
        Macros = { new MacroDef { Key = "F1", Send = "kill", OnlyFor = "Eldran" } },
    };

    private static ProfileLayer Character(string name) => new() { Kind = LayerKind.Character, Name = name };

    [Fact]
    public void ANarrowedRuleReachesOnlyTheCharactersItNames()
    {
        EffectiveProfile eldran = ProfileResolver.Resolve(new[] { Account(), Character("Eldran") });
        Assert.Equal(new[] { "all", "two" }, eldran.Triggers.Select(t => t.Name).OrderBy(n => n));
        Assert.Empty(eldran.Aliases);
        Assert.Empty(eldran.Timers);
        Assert.Equal("walk", Assert.Single(eldran.Sequences).Name);
        Assert.Equal("F1", Assert.Single(eldran.Macros).Key);

        EffectiveProfile nille = ProfileResolver.Resolve(new[] { Account(), Character("Nille") });
        Assert.Equal(new[] { "all", "two" }, nille.Triggers.Select(t => t.Name).OrderBy(n => n));
        Assert.Equal("gd", Assert.Single(nille.Aliases).Name);
        Assert.Empty(nille.Sequences);
        Assert.Empty(nille.Macros);

        EffectiveProfile goran = ProfileResolver.Resolve(new[] { Account(), Character("Goran") });
        Assert.Equal("all", Assert.Single(goran.Triggers).Name);
        Assert.Equal("tick", Assert.Single(goran.Timers).Name);
    }

    [Fact]
    public void TheCharacterPassedInBeatsTheLayersStaleName()
    {
        // a character renamed on disk keeps its old Name in the file until it is next saved;
        // the store resolves by the folder's name, which is what OnlyFor is written against
        EffectiveProfile e = ProfileResolver.Resolve(new[] { Account(), Character("OldName") }, "Nille");
        Assert.Equal("gd", Assert.Single(e.Aliases).Name);
    }

    [Fact]
    public void AnAccountTabHasNobodySoNarrowedRulesStayOut()
    {
        EffectiveProfile e = ProfileResolver.Resolve(new[] { Account() });
        Assert.Equal("all", Assert.Single(e.Triggers).Name);
        Assert.Empty(e.Aliases);
        Assert.Empty(e.Macros);
    }

    [Fact]
    public void ARuleForSomeoneElseDoesNotOverrideTheSameNameFromAbove()
    {
        // left out entirely: the global 'greet' still runs for Goran, the account's narrowed
        // 'greet' replaces it only for Eldran
        var g = new ProfileLayer { Kind = LayerKind.Global, Triggers = { new TriggerDef { Name = "greet", Send = "wave" } } };
        var a = new ProfileLayer { Kind = LayerKind.Account,
            Triggers = { new TriggerDef { Name = "greet", Send = "bow", OnlyFor = "Eldran" } } };
        Assert.Equal("wave", Assert.Single(ProfileResolver.Resolve(new[] { g, a, Character("Goran") }).Triggers).Send);
        Assert.Equal("bow", Assert.Single(ProfileResolver.Resolve(new[] { g, a, Character("Eldran") }).Triggers).Send);
    }

    [Fact]
    public void TheStoreResolvesACharacterByItsFolderName()
    {
        var store = new ProfileStore(_root);
        store.SaveMud("3Scapes", new ProfileLayer());
        store.SaveAccount("3Scapes", "Joakim", Account());
        store.SaveCharacter("3Scapes", "Joakim", "Nille", new ProfileLayer());
        store.SaveCharacter("3Scapes", "Joakim", "Goran", new ProfileLayer());
        Assert.Equal("gd", Assert.Single(store.ResolveCharacter("3Scapes", "Joakim", "Nille").Aliases).Name);
        Assert.Empty(store.ResolveCharacter("3Scapes", "Joakim", "Goran").Aliases);
        Assert.Empty(store.ResolveAccount("3Scapes", "Joakim").Aliases);

        // renamed in the sidebar: the folder moves, the file still says "Nil" until next saved
        store.SaveCharacter("3Scapes", "Joakim", "Nil", new ProfileLayer());
        store.DeleteCharacter("3Scapes", "Joakim", "Nille");
        store.RenameCharacter("3Scapes", "Joakim", "Nil", "Nille");
        Assert.Equal("Nil", store.LoadCharacter("3Scapes", "Joakim", "Nille")!.Name);
        Assert.Equal("gd", Assert.Single(store.ResolveCharacter("3Scapes", "Joakim", "Nille").Aliases).Name);
    }

    [Fact]
    public void OnlyForRoundTripsAndAnUnnarrowedRuleWritesNoField()
    {
        string json = ProfileStore.Serialize(Account());
        ProfileLayer back = ProfileStore.Deserialize(json);
        Assert.Equal("Eldran, Nille", back.Triggers.Single(t => t.Name == "two").OnlyFor);
        Assert.Null(back.Triggers.Single(t => t.Name == "all").OnlyFor);
        Assert.Equal("Eldran", back.Macros[0].OnlyFor);
        Assert.Equal("Eldran", back.Sequences[0].OnlyFor);
        // a profile with no narrowed rules is byte-for-byte what it was before OnlyFor existed
        string plain = ProfileStore.Serialize(new ProfileLayer { Triggers = { new TriggerDef { Name = "a" } } });
        Assert.DoesNotContain("OnlyFor", plain);
    }

    // ---- moving -------------------------------------------------------------------------

    [Fact]
    public void AMovedRuleKeepsItsNameOrIsRenamedRatherThanOverwrite()
    {
        var layer = new ProfileLayer { Triggers = { new TriggerDef { Name = "hp", Send = "old" },
                                                    new TriggerDef { Name = "hp (2)" } } };
        RuleMoved r = RuleMove.Add(layer, RuleHome.Global, new TriggerDef { Name = "hp", Send = "new" });
        Assert.Equal("hp (3)", r.Name);
        Assert.True(r.Renamed);
        Assert.Equal("old", layer.Triggers[0].Send);                    // nothing overwritten
        Assert.Equal("new", layer.Triggers.Single(t => t.Name == "hp (3)").Send);

        RuleMoved fresh = RuleMove.Add(layer, RuleHome.Global, new AliasDef { Name = "gd" });
        Assert.False(fresh.Renamed);
        Assert.Equal("gd", fresh.Name);
        Assert.Equal("t", RuleMove.Add(layer, RuleHome.Global, new TimerDef { Name = "t" }).Name);
        Assert.Equal("w", RuleMove.Add(layer, RuleHome.Global, new SequenceSpec { Name = "w" }).Name);
        Assert.Equal("w (2)", RuleMove.Add(layer, RuleHome.Global, new SequenceSpec { Name = "w" }).Name);
        Assert.Equal("t (2)", RuleMove.Add(layer, RuleHome.Global, new TimerDef { Name = "t" }).Name);
        Assert.Equal("gd (2)", RuleMove.Add(layer, RuleHome.Global, new AliasDef { Name = "gd" }).Name);
        Assert.Throws<ArgumentException>(() => RuleMove.Add(layer, RuleHome.Global, "not a rule"));
    }

    [Fact]
    public void AMovedMacroReplacesTheSameKey()
    {
        var layer = new ProfileLayer { Macros = { new MacroDef { Key = "f1", Send = "old" }, new MacroDef { Key = "F2" } } };
        RuleMoved r = RuleMove.Add(layer, RuleHome.Global, new MacroDef { Key = "F1 ", Send = "new" });
        Assert.True(r.Replaced);
        Assert.Equal(new[] { "F2", "F1 " }, layer.Macros.Select(m => m.Key));
        Assert.False(RuleMove.Add(layer, RuleHome.Global, new MacroDef { Key = "F3" }).Replaced);
    }

    [Fact]
    public void AMoveLiftsASuppressionOfTheSameName()
    {
        var layer = new ProfileLayer { Suppress = { "hp", "other" } };
        RuleMove.Add(layer, RuleHome.Global, new TriggerDef { Name = "hp" });
        Assert.Equal(new[] { "other" }, layer.Suppress);
    }

    [Fact]
    public void TheStoreListsEveryHomeInPickerOrder()
    {
        var store = new ProfileStore(_root);
        store.SaveMud("3Scapes", new ProfileLayer());
        store.SaveAccount("3Scapes", "Joakim", new ProfileLayer());
        store.SaveCharacter("3Scapes", "Joakim", "Nille", new ProfileLayer());
        store.SaveCharacter("3Scapes", "Joakim", "Eldran", new ProfileLayer());
        store.SaveCharacter("3Scapes", null, "Solo", new ProfileLayer());
        Assert.Equal(new[]
        {
            "Global - every world",
            "MUD 3Scapes - everyone on it",
            "Account Joakim (3Scapes) - all its characters",
            "Character Eldran (Joakim, 3Scapes)",
            "Character Nille (Joakim, 3Scapes)",
            "Character Solo (3Scapes)",
        }, store.RuleHomes().Select(h => h.Label));
        Assert.Equal(new[] { "Eldran", "Nille" }, store.CharactersUnder(new RuleHome(LayerKind.Account, "3Scapes", "Joakim")));
        Assert.Equal(new[] { "Eldran", "Nille", "Solo" }, store.CharactersUnder(new RuleHome(LayerKind.Mud, "3Scapes")));
        Assert.Equal(new[] { "Eldran", "Nille", "Solo" }, store.CharactersUnder(RuleHome.Global));
        Assert.Equal(new[] { "Solo" }, store.CharactersUnder(new RuleHome(LayerKind.Character, "3Scapes", null, "Solo")));
    }

    [Fact]
    public void MoveRulesWritesEachTargetOnceAndKeepsWhatWasThere()
    {
        var store = new ProfileStore(_root);
        store.SaveMud("3Scapes", new ProfileLayer { Host = "3scapes.org" });
        store.SaveAccount("3Scapes", "Joakim", new ProfileLayer { Aliases = { new AliasDef { Name = "keep" } } });
        store.SaveCharacter("3Scapes", "Joakim", "Nille", new ProfileLayer { Username = "nille" });
        var nille = new RuleHome(LayerKind.Character, "3Scapes", "Joakim", "Nille");
        var account = new RuleHome(LayerKind.Account, "3Scapes", "Joakim");

        IReadOnlyList<RuleMoved> done = store.MoveRules(new (RuleHome, object)[]
        {
            (nille, new TriggerDef { Name = "hp", Send = "a" }),
            (nille, new TriggerDef { Name = "hp", Send = "b" }),
            (account, new AliasDef { Name = "gd" }),
            (RuleHome.Global, new MacroDef { Key = "F5", Send = "x" }),
        });

        Assert.Equal(new[] { "hp", "hp (2)", "gd", "F5" }, done.Select(d => d.Name));
        ProfileLayer n = store.LoadCharacter("3Scapes", "Joakim", "Nille")!;
        Assert.Equal("nille", n.Username);                              // the rest of the layer kept
        Assert.Equal(new[] { "a", "b" }, n.Triggers.Select(t => t.Send));
        Assert.Equal(new[] { "keep", "gd" }, store.LoadAccount("3Scapes", "Joakim")!.Aliases.Select(a => a.Name));
        Assert.Equal("F5", Assert.Single(store.LoadGlobal().Macros).Key);   // global created on demand
        Assert.Equal("3scapes.org", store.LoadMud("3Scapes")!.Host);

        // a character that has no file yet gets one
        var fresh = new RuleHome(LayerKind.Character, "3Scapes", null, "Newbie");
        store.MoveRules(new (RuleHome, object)[] { (fresh, new TimerDef { Name = "t" }) });
        Assert.Equal("t", Assert.Single(store.LoadCharacter("3Scapes", null, "Newbie")!.Timers).Name);
        Assert.Equal(LayerKind.Character, store.LoadHome(fresh).Kind);
    }
}
