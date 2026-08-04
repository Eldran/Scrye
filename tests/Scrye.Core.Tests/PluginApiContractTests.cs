using System.Reflection;
using Scrye.Core.Plugins;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The plugin API contract: version ranges, the load gate they drive, the theme-token
/// vocabulary, and the cost/failure accounting behind quarantine. These are the pieces a
/// published plugin depends on, so they are worth pinning down harder than most of the engine.
/// </summary>
public class PluginApiContractTests
{
    // ---- the assembly boundary -----------------------------------------------

    [Fact]
    public void ContractTypesLiveInTheContractsAssembly()
    {
        // The namespace is shared with Scrye.Core, so a type can drift across the boundary
        // without anything looking wrong in source. This is what notices.
        foreach (Type t in new[]
                 {
                     typeof(ScryeApi), typeof(ApiRange), typeof(ThemeToken), typeof(PluginPermissions),
                     typeof(PluginManifest), typeof(PluginRequires), typeof(IPluginHost),
                     typeof(PanelSpec), typeof(WidgetSpec), typeof(PanelTabSpec),
                 })
            Assert.Equal("Scrye.PluginContracts", t.Assembly.GetName().Name);
    }

    [Fact]
    public void ContractsAssemblyDependsOnNothingOfOurs()
    {
        // The whole value of the split is that referencing the plugin API costs you nothing.
        // The moment a contract type reaches for something in the engine, that stops being true
        // — and it would only show up as a mysteriously large dependency for plugin authors.
        AssemblyName[] referenced = typeof(ScryeApi).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced,
            a => a.Name is not null && a.Name.StartsWith("Scrye.", StringComparison.Ordinal));
    }

    [Fact]
    public void ContractsAssemblyVersionTracksTheApiVersion()
    {
        // The package version IS the API version — that separation from the app version is the
        // reason the assembly exists, and the two silently drifting would undo it.
        Version assembly = typeof(ScryeApi).Assembly.GetName().Version!;

        Assert.Equal(ScryeApi.Current.Major, assembly.Major);
        Assert.Equal(ScryeApi.Current.Minor, assembly.Minor);
    }

    [Fact]
    public void HostSideTypesStayOutOfTheContract()
    {
        // Anything that needs a file path, a live session or an accounting ledger is engine, not
        // contract. Keeping these in Core is what stops the contracts package growing into a
        // second copy of the engine.
        foreach (Type t in new[]
                 {
                     typeof(PluginCatalog), typeof(PluginDescriptor), typeof(PluginDataStore),
                     typeof(PluginPackage), typeof(PluginDiagnostics), typeof(TimerWheel),
                 })
            Assert.Equal("Scrye.Core", t.Assembly.GetName().Name);
    }

    // ---- ApiRange parsing ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentRangeIsUnconstrainedNotAnError(string? spec)
    {
        // The compatibility of every plugin written before `requires` existed depends on this.
        Assert.True(ApiRange.TryParse(spec, out ApiRange? range, out string error));
        Assert.Null(range);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData(">=1.0", 1, 0, true)]
    [InlineData(">=1.0", 0, 9, false)]
    [InlineData(">1.0", 1, 0, false)]
    [InlineData(">1.0", 1, 1, true)]
    [InlineData("<2.0", 1, 9, true)]
    [InlineData("<2.0", 2, 0, false)]
    [InlineData("<=2.0", 2, 0, true)]
    [InlineData("=1.1", 1, 1, true)]
    [InlineData("=1.1", 1, 2, false)]
    public void OperatorsCompareAsWritten(string spec, int major, int minor, bool allowed)
    {
        Assert.True(ApiRange.TryParse(spec, out ApiRange? range, out _));
        Assert.NotNull(range);
        Assert.Equal(allowed, range!.Allows(new Version(major, minor)));
    }

    [Fact]
    public void ConstraintsCombineWithAnd()
    {
        Assert.True(ApiRange.TryParse(">=1.1 <2.0", out ApiRange? range, out _));
        Assert.False(range!.Allows(new Version(1, 0)));   // below the floor
        Assert.True(range.Allows(new Version(1, 1)));
        Assert.True(range.Allows(new Version(1, 9)));
        Assert.False(range.Allows(new Version(2, 0)));    // at the ceiling, which is exclusive
    }

    [Fact]
    public void BareVersionMeansAtLeast()
    {
        Assert.True(ApiRange.TryParse("1.1", out ApiRange? range, out _));
        Assert.False(range!.Allows(new Version(1, 0)));
        Assert.True(range.Allows(new Version(1, 1)));
        Assert.True(range.Allows(new Version(3, 0)));
    }

    [Fact]
    public void MajorOnlyVersionIsTreatedAsPointZero()
    {
        Assert.True(ApiRange.TryParse(">=1 <2", out ApiRange? range, out _));
        Assert.True(range!.Allows(new Version(1, 0)));
        Assert.True(range.Allows(new Version(1, 7)));
        Assert.False(range.Allows(new Version(2, 0)));
    }

    [Fact]
    public void BuildAndRevisionAreIgnoredWhenComparing()
    {
        // A client version of 1.1.4 must satisfy ">=1.1" — the API version is major.minor only.
        Assert.True(ApiRange.TryParse(">=1.1 <2.0", out ApiRange? range, out _));
        Assert.True(range!.Allows(new Version(1, 1, 4)));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData(">=")]
    [InlineData(">=1.x")]
    public void MalformedRangeIsAnErrorNotSilentlyIgnored(string spec)
    {
        // Guessing what a broken constraint meant is worse than telling the author it broke.
        Assert.False(ApiRange.TryParse(spec, out ApiRange? range, out string error));
        Assert.Null(range);
        Assert.NotEqual("", error);
    }

    // ---- the load gate -------------------------------------------------------

    private static PluginDescriptor Descriptor(string? requiresApi) =>
        new(new PluginManifest
        {
            Id = "test.plugin",
            Requires = requiresApi is null ? null : new PluginRequires { ScryeApi = requiresApi },
        }, "/plugins/test.plugin");

    [Fact]
    public void PluginWithNoDeclaredRangeLoads()
    {
        Assert.True(Descriptor(null).IsApiCompatible(out string reason));
        Assert.Equal("", reason);
    }

    [Fact]
    public void CurrentBuildSatisfiesTheRangeTheScaffoldWrites()
    {
        // The "New plugin" button writes ">=<current> <next major>.0". If that ever fails to
        // load on the build that wrote it, the scaffold is broken.
        string scaffold = $">={ScryeApi.CurrentText} <{ScryeApi.Current.Major + 1}.0";
        Assert.True(Descriptor(scaffold).IsApiCompatible(out _));
    }

    [Fact]
    public void FutureRequirementIsRefusedWithAReadableReason()
    {
        var future = new Version(ScryeApi.Current.Major + 5, 0);
        PluginDescriptor d = Descriptor($">={future.Major}.0");

        Assert.False(d.IsApiCompatible(out string reason));
        Assert.Contains(ScryeApi.CurrentText, reason);     // says what this build has
        Assert.Contains($"{future.Major}.0", reason);      // and what the plugin wanted
    }

    [Fact]
    public void MalformedRequirementRefusesRatherThanLoadingHopefully()
    {
        Assert.False(Descriptor("not-a-range").IsApiCompatible(out string reason));
        Assert.Contains("not a valid range", reason);
    }

    [Fact]
    public void PermissionsDefaultToEmptyNeverNull()
    {
        Assert.Empty(Descriptor(null).Permissions);
    }

    // ---- theme tokens --------------------------------------------------------

    [Fact]
    public void EveryDeclaredTokenIsRecognised()
    {
        foreach (string token in ThemeToken.All)
        {
            Assert.True(ThemeToken.IsToken(token), token);
            Assert.True(ThemeToken.IsColour(token), token);
        }
    }

    [Theory]
    [InlineData("ACCENT")]
    [InlineData("Warning")]
    [InlineData("  dim  ")]
    public void TokenLookupIsCaseAndWhitespaceInsensitive(string value)
    {
        Assert.True(ThemeToken.IsToken(value));
        Assert.NotNull(ThemeToken.Normalize(value));
    }

    [Theory]
    [InlineData("#RRGGBB-ish", false)]
    [InlineData("#12345", false)]
    [InlineData("#123456", true)]
    [InlineData("#ABCDEF", true)]
    public void HexLiteralsAreColoursButNeverTokens(string value, bool isColour)
    {
        Assert.False(ThemeToken.IsToken(value));
        Assert.Equal(isColour, ThemeToken.IsColour(value));
    }

    [Fact]
    public void UnknownNameIsNeitherTokenNorColour()
    {
        Assert.False(ThemeToken.IsToken("chartreuse"));
        Assert.False(ThemeToken.IsColour("chartreuse"));
        Assert.Null(ThemeToken.Normalize("chartreuse"));
    }

    // ---- diagnostics and quarantine ------------------------------------------

    [Fact]
    public void ConsecutiveFailuresQuarantineAndSuccessResetsTheStreak()
    {
        var reports = new List<string>();
        var diag = new PluginDiagnostics(reports.Add);

        // One short of the threshold, then a success: the streak must reset.
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures - 1; i++)
            Assert.False(diag.RecordFailure("p", "onLine", "boom"));
        diag.RecordSuccess("p");
        Assert.False(diag.IsQuarantined("p"));

        // A fresh full streak does quarantine.
        bool tripped = false;
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures; i++)
            tripped |= diag.RecordFailure("p", "onLine", "boom");

        Assert.True(tripped);
        Assert.True(diag.IsQuarantined("p"));
        Assert.Contains(reports, r => r.Contains("unloading"));
    }

    [Fact]
    public void QuarantineIsReportedOnceNotPerFailure()
    {
        var reports = new List<string>();
        var diag = new PluginDiagnostics(reports.Add);

        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures * 3; i++)
            diag.RecordFailure("p", "onLine", "boom");

        Assert.Single(reports);
    }

    [Fact]
    public void QuarantinedIdIsDrainedExactlyOnce()
    {
        // The manager unloads from this list after its dispatch loop; handing the same id back
        // twice would make it try to unload a runtime that is already gone.
        var diag = new PluginDiagnostics(_ => { });
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures; i++)
            diag.RecordFailure("p", "onLine", "boom");

        Assert.Equal(new[] { "p" }, diag.TakeQuarantined());
        Assert.Empty(diag.TakeQuarantined());
    }

    [Fact]
    public void ResetClearsQuarantineSoReloadCanRescueAPlugin()
    {
        var diag = new PluginDiagnostics(_ => { });
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures; i++)
            diag.RecordFailure("p", "onLine", "boom");
        Assert.True(diag.IsQuarantined("p"));

        diag.Reset("p");

        Assert.False(diag.IsQuarantined("p"));
        Assert.Equal(0, diag.Get("p").Failures);
    }

    [Fact]
    public void OnePluginsFailuresDoNotQuarantineAnother()
    {
        var diag = new PluginDiagnostics(_ => { });
        for (int i = 0; i < PluginDiagnostics.QuarantineAfterConsecutiveFailures; i++)
            diag.RecordFailure("bad", "onLine", "boom");

        Assert.True(diag.IsQuarantined("bad"));
        Assert.False(diag.IsQuarantined("good"));
    }

    [Fact]
    public void HealthSummaryIsNullWhenNothingIsWrong()
    {
        var diag = new PluginDiagnostics(_ => { });
        diag.RecordCall("p", elapsedTicks: 0);

        PluginHealth h = diag.Get("p");
        Assert.Null(h.Summary);          // a healthy plugin shows no warning row at all
        Assert.Equal(1L, h.Calls);
        Assert.False(h.Quarantined);
    }

    [Fact]
    public void SlowCallsAreCountedAndSummarised()
    {
        var diag = new PluginDiagnostics(_ => { });
        long slowTicks = (long)(System.Diagnostics.Stopwatch.Frequency
                                * (PluginDiagnostics.SlowCallMs + 10) / 1000.0);
        diag.RecordCall("p", slowTicks);

        PluginHealth h = diag.Get("p");
        Assert.Equal(1, h.SlowCalls);
        Assert.True(h.MaxMs >= PluginDiagnostics.SlowCallMs);
        Assert.Contains("slow", h.Summary!);
    }

    [Fact]
    public void SnapshotIsPublishedForTheUiThread()
    {
        var diag = new PluginDiagnostics(_ => { });
        diag.RecordFailure("p", "onLine", "boom");   // an interesting event publishes

        PluginHealth[] snapshot = diag.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("p", snapshot[0].PluginId);
        Assert.Equal(1, snapshot[0].Failures);
    }

    [Fact]
    public void UnknownPluginHasZeroedHealthRatherThanThrowing()
    {
        var diag = new PluginDiagnostics(_ => { });
        PluginHealth h = diag.Get("never-ran");

        Assert.Equal(0L, h.Calls);
        Assert.Equal(0d, h.AverageMs);
        Assert.Null(h.Summary);
    }
}
