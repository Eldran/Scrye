using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrye.Core.Profiles;

/// <summary>
/// Loads/saves profile layers as JSON and resolves them to an
/// <see cref="EffectiveProfile"/>. Supports the full folder cascade
/// (global -> mud -> account -> character) and a simpler flat "world" model the
/// world-manager UI uses: <c>&lt;root&gt;/global.json</c> plus
/// <c>&lt;root&gt;/&lt;world&gt;/world.json</c>, resolved as [global, world].
/// JSON (System.Text.Json) — no external dependency; null scalars omitted.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    public ProfileStore(string root) => _root = root;

    public string Root => _root;

    public static string Serialize(ProfileLayer layer) => JsonSerializer.Serialize(layer, Options);
    public static ProfileLayer Deserialize(string json) =>
        JsonSerializer.Deserialize<ProfileLayer>(json, Options) ?? new ProfileLayer();

    public ProfileLayer LoadFile(string path) => Deserialize(File.ReadAllText(path));
    public void SaveFile(string path, ProfileLayer layer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(layer));
    }

    // ---- flat "world" model (used by the world-manager UI) -------------------

    private string GlobalPath => Path.Combine(_root, "global.json");
    private string WorldPath(string name) => Path.Combine(_root, name, "world.json");

    public ProfileLayer LoadGlobal() =>
        File.Exists(GlobalPath) ? LoadFile(GlobalPath) : new ProfileLayer { Kind = LayerKind.Global, Name = "global" };

    public void SaveGlobal(ProfileLayer g) { g.Kind = LayerKind.Global; SaveFile(GlobalPath, g); }

    public IReadOnlyList<string> ListWorlds() =>
        Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
                .Where(d => File.Exists(Path.Combine(d, "world.json")))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

    public ProfileLayer? LoadWorld(string name)
    {
        string p = WorldPath(name);
        return File.Exists(p) ? LoadFile(p) : null;
    }

    public void SaveWorld(string name, ProfileLayer layer)
    {
        layer.Kind = LayerKind.Mud;
        layer.Name = name;
        SaveFile(WorldPath(name), layer);
    }

    public void DeleteWorld(string name)
    {
        string dir = Path.Combine(_root, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Resolve a flat world to its effective profile: [global, world].</summary>
    public EffectiveProfile ResolveWorld(string name)
    {
        var chain = new List<ProfileLayer>();
        if (File.Exists(GlobalPath)) chain.Add(LoadFile(GlobalPath));
        ProfileLayer? w = LoadWorld(name);
        if (w is not null) chain.Add(w);
        return ProfileResolver.Resolve(chain);
    }

    // ---- hierarchical model (mud -> [account] -> character) ------------------
    //
    // profiles/
    //   global.json
    //   <mud>/mud.json
    //   <mud>/<character>/character.json             (character directly on the MUD)
    //   <mud>/<account>/account.json
    //   <mud>/<account>/<character>/character.json   (character under an account)
    //
    // A subfolder's identity is the json file inside it, so accounts and
    // account-less characters coexist under the same MUD folder.

    private string MudDir(string mud) => Path.Combine(_root, mud);
    private string MudFile(string mud) => Path.Combine(MudDir(mud), "mud.json");
    private string AccountDir(string mud, string account) => Path.Combine(_root, mud, account);
    private string AccountFile(string mud, string account) => Path.Combine(AccountDir(mud, account), "account.json");
    private string CharacterDir(string mud, string? account, string character) =>
        string.IsNullOrEmpty(account) ? Path.Combine(_root, mud, character) : Path.Combine(_root, mud, account, character);
    private string CharacterFile(string mud, string? account, string character) =>
        Path.Combine(CharacterDir(mud, account, character), "character.json");

    public IReadOnlyList<string> ListMuds() => ListDirsWith(_root, "mud.json");
    public IReadOnlyList<string> ListAccounts(string mud) => ListDirsWith(MudDir(mud), "account.json");
    public IReadOnlyList<string> ListCharacters(string mud, string? account = null) =>
        ListDirsWith(string.IsNullOrEmpty(account) ? MudDir(mud) : AccountDir(mud, account), "character.json");

    private static IReadOnlyList<string> ListDirsWith(string parent, string marker) =>
        Directory.Exists(parent)
            ? Directory.GetDirectories(parent)
                .Where(d => File.Exists(Path.Combine(d, marker)))
                .Select(d => Path.GetFileName(d)!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();

    public ProfileLayer? LoadMud(string mud) => LoadIfExists(MudFile(mud));
    public ProfileLayer? LoadAccount(string mud, string account) => LoadIfExists(AccountFile(mud, account));
    public ProfileLayer? LoadCharacter(string mud, string? account, string character) =>
        LoadIfExists(CharacterFile(mud, account, character));

    private ProfileLayer? LoadIfExists(string path) => File.Exists(path) ? LoadFile(path) : null;

    public void SaveMud(string mud, ProfileLayer layer)
    {
        layer.Kind = LayerKind.Mud; layer.Name = mud;
        SaveFile(MudFile(mud), layer);
    }

    public void SaveAccount(string mud, string account, ProfileLayer layer)
    {
        layer.Kind = LayerKind.Account; layer.Name = account;
        SaveFile(AccountFile(mud, account), layer);
    }

    public void SaveCharacter(string mud, string? account, string character, ProfileLayer layer)
    {
        layer.Kind = LayerKind.Character; layer.Name = character;
        SaveFile(CharacterFile(mud, account, character), layer);
    }

    /// <summary>Delete a MUD and everything under it (accounts + characters).</summary>
    public void DeleteMud(string mud) => DeleteDir(MudDir(mud));
    /// <summary>Delete an account and its characters.</summary>
    public void DeleteAccount(string mud, string account) => DeleteDir(AccountDir(mud, account));
    public void DeleteCharacter(string mud, string? account, string character) =>
        DeleteDir(CharacterDir(mud, account, character));

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>Rename a MUD folder in place — accounts and characters move with it.</summary>
    public void RenameMud(string from, string to) => MoveDir(MudDir(from), MudDir(to));
    public void RenameAccount(string mud, string from, string to) => MoveDir(AccountDir(mud, from), AccountDir(mud, to));
    public void RenameCharacter(string mud, string? account, string from, string to) =>
        MoveDir(CharacterDir(mud, account, from), CharacterDir(mud, account, to));

    private static void MoveDir(string from, string to)
    {
        if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to);
    }

    /// <summary>Resolve a bare MUD (no account/character): [global, mud].</summary>
    public EffectiveProfile ResolveMud(string mud)
    {
        var chain = new List<ProfileLayer>();
        AddIfExists(chain, GlobalPath);
        AddIfExists(chain, MudFile(mud));
        return ProfileResolver.Resolve(chain);
    }

    /// <summary>Resolve an account (no character): [global, mud, account].</summary>
    public EffectiveProfile ResolveAccount(string mud, string account)
    {
        var chain = new List<ProfileLayer>();
        AddIfExists(chain, GlobalPath);
        AddIfExists(chain, MudFile(mud));
        AddIfExists(chain, AccountFile(mud, account));
        return ProfileResolver.Resolve(chain);
    }

    // ---- full folder cascade (global -> mud -> account -> character) ---------

    public EffectiveProfile ResolveCharacter(string mud, string? account, string character)
    {
        var chain = new List<ProfileLayer>();
        AddIfExists(chain, GlobalPath);
        AddIfExists(chain, Path.Combine(_root, mud, "mud.json"));

        string baseDir = Path.Combine(_root, mud);
        if (!string.IsNullOrEmpty(account))
        {
            AddIfExists(chain, Path.Combine(_root, mud, account, "account.json"));
            baseDir = Path.Combine(_root, mud, account);
        }
        AddIfExists(chain, Path.Combine(baseDir, character, "character.json"));
        return ProfileResolver.Resolve(chain);
    }

    private void AddIfExists(List<ProfileLayer> chain, string path)
    {
        if (File.Exists(path)) chain.Add(LoadFile(path));
    }
}
