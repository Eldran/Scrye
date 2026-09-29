namespace Scrye.Core.Plugins;

/// <summary>
/// Installs or updates one plugin from the catalogue.
///
/// <para>All-or-nothing. Every file is downloaded into memory and checked against the size and
/// SHA-256 the index gives before anything touches disk; then the plugin is written into a
/// hidden staging folder beside its destination and swapped in with two renames, the old copy
/// kept until the new one is in place. A failed download, a wrong hash or a locked folder
/// leaves the plugin exactly as it was - a half-updated plugin is worse than an old one.</para>
///
/// <para>Where it goes: over the user-folder copy that is there already (whatever its folder is
/// called), else <c>&lt;userRoot&gt;/&lt;id&gt;</c>. A bundled plugin is never written over -
/// its update lands in the user folder, and discovery (<see cref="PluginCatalog.DiscoverNewest"/>)
/// loads whichever copy is newer. Removing that copy goes back to the bundled one.</para>
///
/// <para>The download is a delegate so this stays free of HTTP and testable offline.</para>
/// </summary>
public static class CatalogInstaller
{
    /// <summary>Fetch the bytes at a URL (the app passes an HttpClient call).</summary>
    public delegate Task<byte[]> Fetch(string url, CancellationToken ct);

    /// <summary>The folder <paramref name="entry"/> installs into.</summary>
    public static string TargetFolder(CatalogEntry entry, PluginDescriptor? installed, string userRoot)
    {
        string root = Path.GetFullPath(userRoot);
        if (installed is not null)
        {
            string have = Path.GetFullPath(installed.FolderPath);
            string parent = Path.GetDirectoryName(have.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "";
            if (string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                              StringComparison.OrdinalIgnoreCase))
                return have;                                   // replace the user copy in place
        }
        return Path.Combine(root, PluginPackage.Sanitize(entry.Id));
    }

    /// <summary>
    /// Download, verify and install <paramref name="entry"/> into <paramref name="target"/>.
    /// Throws with a sentence fit for the user on any failure, having changed nothing.
    /// </summary>
    public static async Task InstallAsync(CatalogIndex index, CatalogEntry entry, string target,
                                          Fetch fetch, CancellationToken ct = default)
    {
        if (entry.TotalSize > CatalogIndex.MaxPluginBytes)
            throw new InvalidDataException($"'{entry.Id}' is larger than a plugin may be");

        // 1. everything into memory, checked
        var got = new List<(CatalogFile File, byte[] Bytes)>();
        foreach (CatalogFile f in entry.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!CatalogIndex.IsSafeRelative(f.Path))
                throw new InvalidDataException($"'{entry.Id}': '{f.Path}' would land outside the plugin folder");
            byte[] bytes;
            try { bytes = await fetch(index.FileUrl(entry, f), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new IOException($"could not download {entry.Id}/{f.Path}: {ex.Message}", ex); }
            if (bytes.LongLength != f.Size)
                throw new InvalidDataException($"{entry.Id}/{f.Path} arrived as {bytes.LongLength} bytes, the catalogue says {f.Size} - nothing was installed");
            if (!string.Equals(CatalogIndex.Sha256Hex(bytes), f.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{entry.Id}/{f.Path} does not match the catalogue's checksum - nothing was installed");
            got.Add((f, bytes));
        }

        // 2. into a hidden staging folder beside the target (same volume, so the swap is a rename)
        string dest = Path.GetFullPath(target);
        string parent = Path.GetDirectoryName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        ?? throw new IOException("no folder to install into");
        Directory.CreateDirectory(parent);
        string name = Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string tag = Guid.NewGuid().ToString("N")[..8];
        string staging = Path.Combine(parent, $".{name}.new-{tag}");
        string old = Path.Combine(parent, $".{name}.old-{tag}");
        try
        {
            string stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach ((CatalogFile f, byte[] bytes) in got)
            {
                string path = Path.GetFullPath(Path.Combine(staging, f.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"'{entry.Id}': '{f.Path}' would land outside the plugin folder");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            }

            // 3. swap: old aside, new in, old gone. A failure between the two renames puts the old back.
            bool hadOld = Directory.Exists(dest);
            if (hadOld) Directory.Move(dest, old);
            try { Directory.Move(staging, dest); }
            catch
            {
                if (hadOld) Directory.Move(old, dest);
                throw;
            }
            if (hadOld) TryDelete(old);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidDataException)
        {
            TryDelete(staging);
            throw new IOException($"could not install '{entry.Id}' into {dest}: {ex.Message}", ex);
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Clear leftovers of an install that died mid-way (a crash between write and swap).
    /// A <c>.name.new-*</c> staging folder always goes. A <c>.name.old-*</c> copy goes only when
    /// <c>name</c> itself is back in place; otherwise it IS the plugin (the swap died between its
    /// two renames) and is moved back rather than deleted.</summary>
    public static void SweepLeftovers(string userRoot)
    {
        string[] hidden;
        try { hidden = Directory.Exists(userRoot) ? Directory.GetDirectories(userRoot, ".*") : Array.Empty<string>(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (string d in hidden)
        {
            string n = Path.GetFileName(d);
            int at = n.LastIndexOf(".new-", StringComparison.Ordinal);
            if (at > 0) { TryDelete(d); continue; }
            at = n.LastIndexOf(".old-", StringComparison.Ordinal);
            if (at <= 1) continue;
            string home = Path.Combine(userRoot, n[1..at]);
            if (Directory.Exists(home)) TryDelete(d);
            else
            {
                try { Directory.Move(d, home); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
