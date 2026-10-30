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

    /// <summary>Read one layer. A file that is not valid JSON throws <see cref="InvalidDataException"/>
    /// naming the file (the JsonException is kept as InnerException), so the caller can tell the
    /// user WHICH profile is broken instead of surfacing a bare parser error.</summary>
    public ProfileLayer LoadFile(string path)
    {
        string json = File.ReadAllText(path);
        try { return Deserialize(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"profile file '{path}' is corrupt and could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>Write one layer atomically: to a temp file beside it, then moved over the old one,
    /// so a crash or full disk mid-write never leaves a truncated profile that will not open.</summary>
    public void SaveFile(string path, ProfileLayer layer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, Serialize(layer));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Is <paramref name="name"/> usable as a MUD / account / character / world folder name?
    /// Names go straight into Path.Combine, so "", ".", "..", a rooted path, a separator or an
    /// invalid file-name char would address a DIFFERENT folder — a character named "." is the
    /// MUD folder itself, and deleting it deleted the whole MUD. <paramref name="reason"/> says
    /// why not, for the UI. Every name-taking method below enforces this (ArgumentException).
    /// </summary>
    public static bool IsValidName(string? name, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(name)) reason = "the name is empty";
        else if (name.Trim() is "." or "..") reason = $"'{name}' is not allowed as a name";
        else if (Path.IsPathRooted(name)) reason = $"'{name}' looks like a path";
        else if (name.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\' }) >= 0)
            reason = $"'{name}' contains a path separator";
        else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) reason = $"'{name}' contains a character that is not allowed in a file name";
        return reason is null;
    }

    private static string Checked(string name, string what)
    {
        if (!IsValidName(name, out string? reason))
            throw new ArgumentException($"invalid {what} name: {reason}", what);
        return name;
    }

    // ---- flat "world" model (used by the world-manager UI) -------------------

    private string GlobalPath => Path.Combine(_root, "global.json");
    private string WorldPath(string name) => Path.Combine(_root, Checked(name, "world"), "world.json");

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
        string dir = Path.Combine(_root, Checked(name, "world"));
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

    // Every name is validated here (see IsValidName) before it reaches the file system.
    private string MudDir(string mud) => Path.Combine(_root, Checked(mud, "mud"));
    private string MudFile(string mud) => Path.Combine(MudDir(mud), "mud.json");
    private string AccountDir(string mud, string account) => Path.Combine(MudDir(mud), Checked(account, "account"));
    private string AccountFile(string mud, string account) => Path.Combine(AccountDir(mud, account), "account.json");
    private string CharacterDir(string mud, string? account, string character) =>
        string.IsNullOrEmpty(account) ? Path.Combine(MudDir(mud), Checked(character, "character"))
                                      : Path.Combine(AccountDir(mud, account), Checked(character, "character"));
    private string CharacterFile(string mud, string? account, string character) =>
        Path.Combine(CharacterDir(mud, account, character), "character.json");

    public IReadOnlyList<string> ListMuds() => ListDirsWith(_root, "mud.json");

    /// <summary>Every plugin id enabled at ANY layer - global, flat worlds, MUDs, accounts,
    /// characters - so "plugins someone uses" can be asked without connecting anything. A
    /// layer that fails to load is skipped: this is a hint for a notice, not a resolve.</summary>
    public ISet<string> AllEnabledPlugins()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(Func<ProfileLayer?> load)
        {
            try { if (load() is { } l) foreach (string id in l.Plugins) ids.Add(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
        Add(LoadGlobal);
        foreach (string w in ListWorlds()) Add(() => LoadWorld(w));
        foreach (string mud in ListMuds())
        {
            Add(() => LoadMud(mud));
            foreach (string ch in ListCharacters(mud)) Add(() => LoadCharacter(mud, null, ch));
            foreach (string acct in ListAccounts(mud))
            {
                Add(() => LoadAccount(mud, acct));
                foreach (string ch in ListCharacters(mud, acct)) Add(() => LoadCharacter(mud, acct, ch));
            }
        }
        return ids;
    }
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

    /// <summary>
    /// Is <paramref name="name"/> already someone's folder - another MUD, or (on a MUD) another
    /// account or account-less character, which share one folder; or (in an account) another
    /// character? Ask before creating a layer or renaming one: saving into a taken folder
    /// writes over the profile that lives there. <paramref name="current"/> is the layer's
    /// name now when renaming, so a rename that only changes the case of its own folder is
    /// not "taken" on a case-blind file system.
    /// </summary>
    public bool NameTaken(LayerKind kind, string? mud, string? account, string name, string? current = null)
    {
        string Dir(string n) => kind switch
        {
            LayerKind.Mud => MudDir(n),
            LayerKind.Account => AccountDir(mud!, n),
            _ => CharacterDir(mud!, account, n),
        };
        return Taken(current is null ? null : Dir(current), Dir(name));
    }

    /// <summary>The words for "what is already called that" in a refusal.</summary>
    public static string TakenMessage(LayerKind kind, string name) => kind switch
    {
        LayerKind.Mud => $"there is already a MUD called '{name}' - pick another name",
        LayerKind.Account => $"'{name}' is already an account or character on this MUD - pick another name",
        _ => $"'{name}' is already taken here (by a character, or an account on this MUD) - pick another name",
    };

    private static bool Taken(string? from, string to)
    {
        if (!Directory.Exists(to)) return false;
        return from is null || !SameFolder(from, to);
    }

    /// <summary>Two spellings of ONE folder: equal ignoring case, and the parent does not list
    /// <paramref name="to"/>'s spelling as a folder of its own - a case-blind file system
    /// (Windows) answers for "3Scapes" with the "3scapes" that is there. On a case-sensitive one
    /// two such folders can both exist, and then they are two.</summary>
    private static bool SameFolder(string from, string to)
    {
        string f = Path.GetFullPath(from), t = Path.GetFullPath(to);
        if (!string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(f, t, StringComparison.Ordinal)) return true;
        string name = Path.GetFileName(t);
        foreach (string d in Directory.GetDirectories(Path.GetDirectoryName(t)!))
            if (string.Equals(Path.GetFileName(d), name, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Rename a layer's folder. A name already taken by another folder REFUSES
    /// (IOException): it used to do nothing, and the save that followed wrote this layer over
    /// the one already living there. A change of case alone goes through a temporary name, so
    /// a case-blind file system really renames it.</summary>
    private static void MoveDir(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        if (Taken(from, to))
            throw new IOException($"'{Path.GetFileName(to)}' is already taken - pick another name");
        if (Directory.Exists(to))
        {
            // the same folder, spelled with other capitals: via a temporary name
            string tmp = from + ".renaming-" + Guid.NewGuid().ToString("N")[..8];
            Directory.Move(from, tmp);
            Directory.Move(tmp, to);
            return;
        }
        Directory.Move(from, to);
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
        AddIfExists(chain, MudFile(mud));
        if (!string.IsNullOrEmpty(account))
            AddIfExists(chain, AccountFile(mud, account));
        AddIfExists(chain, CharacterFile(mud, account, character));
        // the character by its folder name - the one a rule's OnlyFor is written against, and
        // right even when the file's own Name predates a rename
        return ProfileResolver.Resolve(chain, character);
    }

    // ---- where rules live (OnlyFor's companion: moving a rule between layers) ----

    /// <summary>Every layer a rule can live on, in the order a picker shows them: Global, then
    /// per MUD the MUD, its accounts each followed by their characters, then the characters
    /// directly on the MUD.</summary>
    public IReadOnlyList<RuleHome> RuleHomes()
    {
        var homes = new List<RuleHome> { RuleHome.Global };
        foreach (string mud in ListMuds())
        {
            homes.Add(new RuleHome(LayerKind.Mud, mud));
            foreach (string account in ListAccounts(mud))
            {
                homes.Add(new RuleHome(LayerKind.Account, mud, account));
                foreach (string ch in ListCharacters(mud, account))
                    homes.Add(new RuleHome(LayerKind.Character, mud, account, ch));
            }
            foreach (string ch in ListCharacters(mud))
                homes.Add(new RuleHome(LayerKind.Character, mud, null, ch));
        }
        return homes;
    }

    /// <summary>The characters a layer reaches - its own, an account's, a MUD's (on accounts
    /// and directly on it), or every one for Global - for telling the user what names an
    /// OnlyFor there can use.</summary>
    public IReadOnlyList<string> CharactersUnder(RuleHome home) => home.Kind switch
    {
        LayerKind.Character => new[] { home.Character! },
        LayerKind.Account => ListCharacters(home.Mud!, home.Account),
        LayerKind.Mud => ListAccounts(home.Mud!).SelectMany(a => ListCharacters(home.Mud!, a))
                             .Concat(ListCharacters(home.Mud!)).Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
        _ => ListMuds().SelectMany(m => CharactersUnder(new RuleHome(LayerKind.Mud, m)))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(),
    };

    /// <summary>The layer at <paramref name="home"/>, or a new empty one of that kind when it
    /// has no file yet.</summary>
    public ProfileLayer LoadHome(RuleHome home) => home.Kind switch
    {
        LayerKind.Global => File.Exists(GlobalPath) ? LoadFile(GlobalPath) : new ProfileLayer { Kind = LayerKind.Global, Name = "global" },
        LayerKind.Mud => LoadMud(home.Mud!) ?? new ProfileLayer { Kind = LayerKind.Mud },
        LayerKind.Account => LoadAccount(home.Mud!, home.Account!) ?? new ProfileLayer { Kind = LayerKind.Account },
        _ => LoadCharacter(home.Mud!, home.Account, home.Character!) ?? new ProfileLayer { Kind = LayerKind.Character },
    };

    public void SaveHome(RuleHome home, ProfileLayer layer)
    {
        switch (home.Kind)
        {
            case LayerKind.Global: SaveGlobal(layer); break;
            case LayerKind.Mud: SaveMud(home.Mud!, layer); break;
            case LayerKind.Account: SaveAccount(home.Mud!, home.Account!, layer); break;
            default: SaveCharacter(home.Mud!, home.Account, home.Character!, layer); break;
        }
    }

    /// <summary>
    /// Add each rule to its new home and save those layers - one load and one write per
    /// layer, however many rules land on it. Only the ADDING happens here: the caller removes
    /// the rules from where they were by saving that layer afterwards. In that order a failure
    /// part-way costs a duplicate (the rule is in both places), never the rule.
    /// </summary>
    public IReadOnlyList<RuleMoved> MoveRules(IEnumerable<(RuleHome Home, object Rule)> moves)
    {
        var results = new List<RuleMoved>();
        foreach (IGrouping<RuleHome, (RuleHome Home, object Rule)> group in moves.GroupBy(m => m.Home))
        {
            ProfileLayer layer = LoadHome(group.Key);
            foreach ((RuleHome home, object rule) in group) results.Add(RuleMove.Add(layer, home, rule));
            SaveHome(group.Key, layer);
        }
        return results;
    }

    private void AddIfExists(List<ProfileLayer> chain, string path)
    {
        if (File.Exists(path)) chain.Add(LoadFile(path));
    }
}
