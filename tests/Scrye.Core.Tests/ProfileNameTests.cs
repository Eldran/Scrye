using Scrye.Core.Profiles;
using Xunit;

namespace Scrye.Core.Tests;

/// <summary>
/// A MUD, account or character can never be given a name that is already another one's
/// folder. A rename onto a taken name used to do nothing, and the save that followed wrote the
/// renamed layer over the one that lived there; creating a new character called like an
/// existing one replaced it the same way.
///
/// <para>Not covered here: SameFolder's first test (the two paths must be equal ignoring case
/// before they can be one folder). It only decides anything on a case-blind file system -
/// renaming Eldran to "nille" on Windows while "Nille" exists - and these tests run on
/// whatever file system the build uses; mutation-tested on Linux, where removing it changes
/// no outcome.</para>
/// </summary>
public sealed class ProfileNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scrye-names-" + Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store;

    public ProfileNameTests()
    {
        _store = new ProfileStore(_root);
        _store.SaveMud("3Scapes", new ProfileLayer { Host = "3scapes.org" });
        _store.SaveMud("Other", new ProfileLayer { Host = "other.org" });
        _store.SaveAccount("3Scapes", "Joakim", new ProfileLayer { Username = "joakim" });
        _store.SaveCharacter("3Scapes", "Joakim", "Eldran", new ProfileLayer { Username = "eldran" });
        _store.SaveCharacter("3Scapes", "Joakim", "Nille", new ProfileLayer { Username = "nille" });
        _store.SaveCharacter("3Scapes", null, "Solo", new ProfileLayer { Username = "solo" });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void RenamingOntoATakenNameRefusesAndTouchesNothing()
    {
        IOException ex = Assert.Throws<IOException>(() => _store.RenameMud("3Scapes", "Other"));
        Assert.Contains("'Other' is already taken", ex.Message);
        Assert.Equal("3scapes.org", _store.LoadMud("3Scapes")!.Host);
        Assert.Equal("other.org", _store.LoadMud("Other")!.Host);

        Assert.Throws<IOException>(() => _store.RenameCharacter("3Scapes", "Joakim", "Eldran", "Nille"));
        Assert.Equal("eldran", _store.LoadCharacter("3Scapes", "Joakim", "Eldran")!.Username);
        Assert.Equal("nille", _store.LoadCharacter("3Scapes", "Joakim", "Nille")!.Username);

        // an account-less character and an account share the MUD's folder
        Assert.Throws<IOException>(() => _store.RenameCharacter("3Scapes", null, "Solo", "Joakim"));
        Assert.Throws<IOException>(() => _store.RenameAccount("3Scapes", "Joakim", "Solo"));
        Assert.Equal("joakim", _store.LoadAccount("3Scapes", "Joakim")!.Username);
        Assert.Equal("solo", _store.LoadCharacter("3Scapes", null, "Solo")!.Username);
    }

    [Fact]
    public void AFreeNameStillRenames()
    {
        _store.RenameCharacter("3Scapes", "Joakim", "Eldran", "Goran");
        Assert.Null(_store.LoadCharacter("3Scapes", "Joakim", "Eldran"));
        Assert.Equal("eldran", _store.LoadCharacter("3Scapes", "Joakim", "Goran")!.Username);
        _store.RenameMud("3Scapes", "Scapes");                          // its accounts move with it
        Assert.Equal("eldran", _store.LoadCharacter("Scapes", "Joakim", "Goran")!.Username);
        _store.RenameMud("Nowhere", "Anything");                        // nothing there: nothing happens
        Assert.Null(_store.LoadMud("Anything"));
    }

    [Fact]
    public void NameTakenSaysWhatSaveWouldOverwrite()
    {
        Assert.True(_store.NameTaken(LayerKind.Mud, null, null, "Other"));
        Assert.False(_store.NameTaken(LayerKind.Mud, null, null, "Fresh"));
        Assert.True(_store.NameTaken(LayerKind.Character, "3Scapes", "Joakim", "Nille"));
        Assert.False(_store.NameTaken(LayerKind.Character, "3Scapes", "Joakim", "Solo"));   // Solo is not in Joakim
        Assert.True(_store.NameTaken(LayerKind.Character, "3Scapes", null, "Joakim"));      // the account's folder
        Assert.True(_store.NameTaken(LayerKind.Account, "3Scapes", null, "Solo"));          // the character's folder
        Assert.False(_store.NameTaken(LayerKind.Account, "3Scapes", null, "Nille"));        // Nille is inside Joakim
        // renaming to its own name is not "taken"
        Assert.False(_store.NameTaken(LayerKind.Character, "3Scapes", "Joakim", "Nille", current: "Nille"));
        Assert.True(_store.NameTaken(LayerKind.Character, "3Scapes", "Joakim", "Nille", current: "Eldran"));
    }

    [Fact]
    public void TheRefusalSaysWhatIsInTheWay()
    {
        Assert.Contains("already a MUD called 'Other'", ProfileStore.TakenMessage(LayerKind.Mud, "Other"));
        Assert.Contains("'Solo' is already an account or character", ProfileStore.TakenMessage(LayerKind.Account, "Solo"));
        Assert.Contains("'Nille' is already taken here", ProfileStore.TakenMessage(LayerKind.Character, "Nille"));
    }

    [Fact]
    public void TwoFoldersThatDifferOnlyInCaseAreTwoWhereTheFileSystemSaysSo()
    {
        // Only meaningful on a case-sensitive file system (Linux), where "nille" can sit beside
        // "Nille": a rename of Eldran to "nille" must refuse there, not walk over it. On a
        // case-blind one (Windows) the second folder cannot exist, so the check is skipped.
        string lower = Path.Combine(_root, "3Scapes", "Joakim", "nille");
        Directory.CreateDirectory(lower);
        bool caseSensitive = !Directory.Exists(Path.Combine(_root, "3Scapes", "Joakim", "NILLE"))
                             && Directory.GetDirectories(Path.Combine(_root, "3Scapes", "Joakim")).Length >= 3;
        if (!caseSensitive) return;
        Assert.Throws<IOException>(() => _store.RenameCharacter("3Scapes", "Joakim", "Eldran", "nille"));
        Assert.True(_store.NameTaken(LayerKind.Character, "3Scapes", "Joakim", "nille", current: "Nille"));
    }
}
