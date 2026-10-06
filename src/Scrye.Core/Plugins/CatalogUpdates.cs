namespace Scrye.Core.Plugins;

/// <summary>
/// Which installed plugins the catalogue has a newer version of - the startup notice's
/// question (Joakim, 6 Oct 2026: a user who restarts Scrye should hear that their plugins
/// have updates, not have to go and look).
///
/// <para>Only plugins someone actually uses are named: every bundled plugin is "installed"
/// for everybody, and a viking update means nothing to a player who never switched the viking
/// plugins on. <c>enabled</c> is the union of every profile layer's plugin list; null names
/// them all. Plugins loaded from the extra folder are left out - they are being worked on
/// there, and the catalogue will not update them anyway.</para>
/// </summary>
public static class CatalogUpdates
{
    /// <summary>One update: the catalogue's entry and the version that loads today.</summary>
    public sealed record Pending(CatalogEntry Entry, string InstalledVersion);

    public static IReadOnlyList<Pending> Find(CatalogIndex index, IEnumerable<PluginDescriptor> installed,
                                              ISet<string>? enabled, string? extraRoot = null)
    {
        string? extra = string.IsNullOrEmpty(extraRoot) ? null : Path.GetFullPath(extraRoot);
        var have = new Dictionary<string, PluginDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginDescriptor d in installed) have[d.Id] = d;

        var found = new List<Pending>();
        foreach (CatalogEntry e in index.Plugins)
        {
            if (!have.TryGetValue(e.Id, out PluginDescriptor? d)) continue;
            if (enabled is not null && !enabled.Contains(e.Id)) continue;
            if (extra is not null
                && Path.GetFullPath(d.FolderPath).StartsWith(extra, StringComparison.OrdinalIgnoreCase)) continue;
            if (CatalogIndex.StatusOf(e, d) == CatalogStatus.UpdateAvailable)
                found.Add(new Pending(e, d.Manifest.Version));
        }
        return found.OrderBy(p => p.Entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"2 plugin updates: 3S Viking Status 2.27.5, 3S Viking Kingdom 1.1.0" -
    /// the first few by name, then "and N more".</summary>
    public static string Describe(IReadOnlyList<Pending> pending, int names = 3)
    {
        if (pending.Count == 0) return "";
        string head = pending.Count == 1 ? "1 plugin update" : $"{pending.Count} plugin updates";
        var shown = pending.Take(names).Select(p =>
            (string.IsNullOrWhiteSpace(p.Entry.Name) ? p.Entry.Id : p.Entry.Name) + " " + p.Entry.Version);
        string list = string.Join(", ", shown);
        if (pending.Count > names) list += $" and {pending.Count - names} more";
        return head + ": " + list;
    }
}
