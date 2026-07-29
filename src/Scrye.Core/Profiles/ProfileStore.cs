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
