using Scrye.Core.Updates;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// The "a new Scrye is out" check: reading <c>catalog/release.json</c>, deciding whether it is
/// newer than this build, and never letting the file choose where the link goes.
/// </summary>
public sealed class ReleaseInfoTests
{
    [Fact]
    public void AReleaseRoundTripsAndLinksToItsTag()
    {
        ReleaseInfo r = ReleaseInfo.FromTag("v1.9.2", "Scrye v1.9.2\n\nClient: multi-line triggers.\n", "2026-09-30");
        ReleaseInfo back = ReleaseInfo.Parse(r.ToJson());

        Assert.Equal("1.9.2", back.Version);
        Assert.Equal("v1.9.2", back.Tag);
        Assert.Equal("Scrye v1.9.2\n\nClient: multi-line triggers.", back.Notes);
        Assert.Equal("https://github.com/Eldran/Scrye/releases/tag/v1.9.2", back.PageUrl);
        Assert.Contains("\"tag\": \"v1.9.2\"", r.ToJson());
    }

    [Theory]
    [InlineData("1.9.2", "1.9.1", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("1.9.1", "1.9.1", false)]
    [InlineData("1.9.0", "1.9.1", false)]
    [InlineData("2.0.0", "2.0.0-beta", true)]
    public void NewerIsDecidedAsVersionsNotText(string release, string running, bool newer)
    {
        Assert.Equal(newer, ReleaseInfo.FromTag("v" + release, null).IsNewerThan(running));
    }

    [Fact]
    public void ALinkInTheFileIsIgnored()
    {
        ReleaseInfo r = ReleaseInfo.Parse(
            "{ \"format\": 1, \"tag\": \"v1.9.2\", \"url\": \"file:///C:/Windows/evil.exe\", \"pageUrl\": \"https://evil.example\" }");

        Assert.Equal("https://github.com/Eldran/Scrye/releases/tag/v1.9.2", r.PageUrl);
        Assert.Equal("1.9.2", r.Version);                                   // taken from the tag
    }

    [Theory]
    [InlineData("{ \"format\": 1, \"tag\": \"../../evil\" }")]
    [InlineData("{ \"format\": 1, \"tag\": \"v1.9.2 && calc\" }")]
    [InlineData("{ \"format\": 1 }")]
    [InlineData("{ \"format\": 2, \"tag\": \"v1.9.2\" }")]
    [InlineData("<html>404</html>")]
    public void AFileThatIsNotAReleaseIsRefused(string json)
    {
        Assert.Throws<InvalidDataException>(() => ReleaseInfo.Parse(json));
    }

    [Fact]
    public void LongNotesAreCutAndASignatureIsDropped()
    {
        string body = new string('x', 5000);
        ReleaseInfo r = ReleaseInfo.FromTag("v2.0.0", body);
        Assert.Equal(ReleaseInfo.MaxNotes, r.Notes!.Length);
        Assert.EndsWith("…", r.Notes);

        ReleaseInfo signed = ReleaseInfo.FromTag("v2.0.0", "Notes\n-----BEGIN PGP SIGNATURE-----\nabc\n-----END PGP SIGNATURE-----");
        Assert.Equal("Notes", signed.Notes);
    }

    [Fact]
    public void TheRunningVersionHasNoCommitSuffix()
    {
        string v = ReleaseInfo.CurrentVersion();
        Assert.DoesNotContain("+", v);
        Assert.Matches(@"^\d+\.\d+\.\d+", v);
    }
}
