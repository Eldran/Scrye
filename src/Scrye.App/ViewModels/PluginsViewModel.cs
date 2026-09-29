using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Scrye.Core.Plugins;
using Scrye.Scripting.Plugins;

namespace Scrye.App.ViewModels;

/// <summary>Backs the per-world plugins-manager panel: lists discovered plugins and their
/// loaded/removable state, and offers reload / enable-disable / remove, plus add workflows
/// (create a starter plugin, open the plugins folder, rescan disk). The mutating actions are
/// routed (by the caller) onto the session loop; this VM refreshes from a snapshot afterward.
/// A second page, the Catalogue, lists the plugins published in the Scrye repo for this MUD and
/// installs or updates them.</summary>
public sealed class PluginsViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PluginInfo>> _list;
    private readonly Action<string, Action> _reload;             // (id, onDone)
    private readonly Action<string, bool, Action> _setEnabled;   // (id, enable, onDone)
    private readonly Action<string, Action> _remove;             // (id, onDone)
    private readonly Action<Action> _rescan;                     // (onDone)
    private readonly Action<Action> _newPlugin;                  // (onDone) — scaffold + rescan
    private readonly Action _openFolder;
    private readonly Func<IReadOnlyList<PluginHealth>>? _health;  // cost/failure snapshot, or null

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = new();
    public RelayCommand RescanCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <param name="health">Optional per-plugin cost/failure snapshot (from
    /// <see cref="PluginManager.Diagnostics"/>). When supplied, rows show why a plugin is slow or
    /// has been quarantined instead of leaving the user to infer it from scrollback.</param>
    /// <param name="mudId">The world the catalogue is filtered for (a plugin's mudIds).</param>
    /// <param name="loadCatalogue">Reads the catalogue index (off the UI thread); null hides the
    /// Catalogue page. The Action collects entries the index had to skip.</param>
    /// <param name="installFromCatalogue">Downloads, verifies and installs one entry into the user
    /// folder (not onto the session loop - the rescan that follows is).</param>
    public PluginsViewModel(Func<IReadOnlyList<PluginInfo>> list,
                            Action<string, Action> reload,
                            Action<string, bool, Action> setEnabled,
                            Action<string, Action> remove,
                            Action<Action> rescan,
                            Action<Action> newPlugin,
                            Action openFolder,
                            Func<IReadOnlyList<PluginHealth>>? health = null,
                            string? mudId = null,
                            Func<Action<string>, CancellationToken, Task<CatalogIndex>>? loadCatalogue = null,
                            Func<CatalogIndex, CatalogEntry, Task>? installFromCatalogue = null)
    {
        _mudId = mudId ?? "*";
        _loadCatalogue = loadCatalogue;
        _install = installFromCatalogue;
        _health = health;
        _list = list;
        _reload = reload;
        _setEnabled = setEnabled;
        _remove = remove;
        _rescan = rescan;
        _newPlugin = newPlugin;
        _openFolder = openFolder;

        RescanCommand = new RelayCommand(() => _rescan(Refresh));
        NewCommand = new RelayCommand(() => _newPlugin(Refresh));
        OpenFolderCommand = new RelayCommand(() => _openFolder());
        CloseCommand = new RelayCommand(Close);
        ShowInstalledCommand = new RelayCommand(() => ShowCatalogue = false);
        ShowCatalogueCommand = new RelayCommand(() => ShowCatalogue = true);
        RefreshCatalogueCommand = new RelayCommand(() => _ = LoadCatalogueAsync());
    }

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        set { if (SetField(ref _isOpen, value) && value) Refresh(); }   // refresh when opened
    }

    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;

    public void Refresh()
    {
        Plugins.Clear();
        IReadOnlyList<PluginHealth> health = _health?.Invoke() ?? Array.Empty<PluginHealth>();
        foreach (PluginInfo p in _list())
        {
            PluginHealth? h = null;
            foreach (PluginHealth candidate in health)
                if (candidate.PluginId == p.Id) { h = candidate; break; }

            Plugins.Add(new PluginRowViewModel(p, h,
                id => _reload(id, Refresh),
                (id, enable) => _setEnabled(id, enable, Refresh),
                id => _remove(id, Refresh)));
        }
        if (_index is not null) RebuildCatalogue();   // what is installed moved: so do the buttons
    }

    // ---- catalogue ------------------------------------------------------------------

    private readonly string _mudId;
    private readonly Func<Action<string>, CancellationToken, Task<CatalogIndex>>? _loadCatalogue;
    private readonly Func<CatalogIndex, CatalogEntry, Task>? _install;
    private CatalogIndex? _index;
    private bool _catalogueLoading;

    public ObservableCollection<CatalogRowViewModel> Catalogue { get; } = new();
    public RelayCommand ShowInstalledCommand { get; }
    public RelayCommand ShowCatalogueCommand { get; }
    public RelayCommand RefreshCatalogueCommand { get; }

    /// <summary>Whether this world offers the Catalogue page at all.</summary>
    public bool HasCatalogue => _loadCatalogue is not null;

    private bool _showCatalogue;
    /// <summary>The Catalogue page is showing (else the installed list). Read on first show.</summary>
    public bool ShowCatalogue
    {
        get => _showCatalogue;
        set
        {
            if (!SetField(ref _showCatalogue, value)) return;
            OnPropertyChanged(nameof(ShowInstalled));
            if (value && _index is null) _ = LoadCatalogueAsync();
        }
    }
    public bool ShowInstalled => !_showCatalogue;

    private string? _catalogueStatus;
    /// <summary>One line under the catalogue: what it is, how it went, or what failed.</summary>
    public string? CatalogueStatus
    {
        get => _catalogueStatus;
        private set => SetField(ref _catalogueStatus, value);
    }

    private async Task LoadCatalogueAsync()
    {
        if (_loadCatalogue is null || _catalogueLoading) return;
        _catalogueLoading = true;
        CatalogueStatus = "Reading the catalogue…";
        var skipped = new List<string>();
        try
        {
            _index = await _loadCatalogue(s => { lock (skipped) skipped.Add(s); }, CancellationToken.None);
            RebuildCatalogue();
            if (skipped.Count > 0) CatalogueStatus += $" · {skipped.Count} entr{(skipped.Count == 1 ? "y" : "ies")} skipped";
        }
        catch (Exception ex)
        {
            CatalogueStatus = ex.Message;
        }
        finally { _catalogueLoading = false; }
    }

    private void RebuildCatalogue()
    {
        Catalogue.Clear();
        if (_index is null) return;
        var have = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginInfo p in _list()) have[p.Id] = p.Version;

        int updates = 0;
        foreach (CatalogEntry e in _index.Plugins.Where(e => e.AppliesTo(_mudId))
                                                 .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var row = new CatalogRowViewModel(e, have.TryGetValue(e.Id, out string? v) ? v : null, InstallFromCatalogue);
            if (row.Status == CatalogStatus.UpdateAvailable) updates++;
            Catalogue.Add(row);
        }
        CatalogueStatus = $"{Catalogue.Count} plugin{(Catalogue.Count == 1 ? "" : "s")} from Scrye {_index.Ref}"
                          + (updates > 0 ? $" · {updates} update{(updates == 1 ? "" : "s")}" : "");
    }

    private async void InstallFromCatalogue(CatalogRowViewModel row)
    {
        if (_install is null || _index is null || row.Busy) return;
        row.Busy = true;
        bool update = row.Status == CatalogStatus.UpdateAvailable;
        try
        {
            await _install(_index, row.Entry);
            string done = update
                ? $"{row.Name} updated to v{row.Entry.Version}"
                : $"{row.Name} v{row.Entry.Version} installed - turn it on under Installed";
            // rescan (on the loop) picks the new copy up and reloads it if it was running
            _rescan(() => { Refresh(); CatalogueStatus = done; });
        }
        catch (Exception ex)
        {
            row.Busy = false;
            CatalogueStatus = ex.Message;
        }
    }
}

/// <summary>One plugin on the Catalogue page.</summary>
public sealed class CatalogRowViewModel : ViewModelBase
{
    public CatalogEntry Entry { get; }
    public CatalogStatus Status { get; }
    public string Name { get; }
    public string Detail { get; }
    public string StateText { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrEmpty(Description);
    public string? PermissionSummary { get; }
    public bool HasPermissions => !string.IsNullOrEmpty(PermissionSummary);
    public bool IsProblem => Status == CatalogStatus.Incompatible;
    public bool HasAction => Status is CatalogStatus.NotInstalled or CatalogStatus.UpdateAvailable;
    public RelayCommand ActCommand { get; }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set
        {
            if (!SetField(ref _busy, value)) return;
            OnPropertyChanged(nameof(ActionLabel));
            ActCommand.RaiseCanExecuteChanged();
        }
    }

    public string ActionLabel => _busy ? "…" : Status == CatalogStatus.UpdateAvailable ? "Update" : "Install";

    public CatalogRowViewModel(CatalogEntry entry, string? installedVersion, Action<CatalogRowViewModel> act)
    {
        Entry = entry;
        Status = CatalogIndex.StatusOfVersion(entry, installedVersion);
        Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name;

        long kb = Math.Max(1, (entry.TotalSize + 1023) / 1024);
        Detail = $"v{entry.Version}"
                 + (string.IsNullOrWhiteSpace(entry.Author) ? "" : $" · {entry.Author}")
                 + $" · {kb:N0} KB";

        entry.IsApiCompatible(out string why);
        StateText = Status switch
        {
            CatalogStatus.NotInstalled => "not installed",
            CatalogStatus.UpToDate => "installed",
            CatalogStatus.UpdateAvailable => $"installed v{installedVersion} → v{entry.Version}",
            CatalogStatus.NewerInstalled => $"you have v{installedVersion}, newer than this",
            _ => "cannot run on this Scrye: " + why,
        };

        // Long manifests (the viking HUD's runs to paragraphs) are cut for the row; the whole
        // text is the tooltip.
        string? d = entry.Description?.Trim();
        Description = d;
        ShortDescription = d is { Length: > 180 } ? d[..177].TrimEnd() + "…" : d;

        if (entry.Permissions.Length > 0)
            PermissionSummary = "Declares: " + string.Join(", ", entry.Permissions
                .OrderByDescending(PluginPermissions.IsSensitive).ThenBy(p => p, StringComparer.Ordinal));

        ActCommand = new RelayCommand(() => act(this), () => HasAction && !_busy);
    }

    public string? ShortDescription { get; }
}

/// <summary>One row in the plugins manager.</summary>
public sealed class PluginRowViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public bool Loaded { get; }
    public bool Removable { get; }
    public string Detail { get; }
    public string ToggleLabel => Loaded ? "Disable" : "Enable";

    /// <summary>Why this plugin cannot load on this build at all (API mismatch), or null.</summary>
    public string? IncompatibleReason { get; }
    public bool HasIncompatibility => !string.IsNullOrEmpty(IncompatibleReason);

    /// <summary>Cost/failure line, shown only when there is something wrong worth surfacing.</summary>
    public string? HealthSummary { get; }
    public bool HasHealthWarning => !string.IsNullOrEmpty(HealthSummary);

    /// <summary>One-line capability summary ("Can: send commands, rewrite output, …").</summary>
    public string? PermissionSummary { get; }
    public bool HasPermissions => !string.IsNullOrEmpty(PermissionSummary);

    /// <summary>The full permission list with descriptions, for the row's tooltip.</summary>
    public string? PermissionDetail { get; }

    public RelayCommand ReloadCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public PluginRowViewModel(PluginInfo info, PluginHealth? health,
                              Action<string> reload, Action<string, bool> setEnabled, Action<string> remove)
    {
        Id = info.Id;
        Name = string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name;
        Loaded = info.Loaded;
        Removable = info.Removable;

        string state = info.Loaded ? "loaded" : info.IncompatibleReason is not null ? "unavailable" : "disabled";
        // Engine label (only when loaded): during the KeraLua soak, which engine a plugin
        // runs on should be visible at a glance, not inferred.
        string engine = string.IsNullOrEmpty(info.Engine) ? "" : $" · {info.Engine}";
        Detail = info.RequiresApi is { Length: > 0 }
            ? $"v{info.Version} · {state}{engine} · needs API {info.RequiresApi}"
            : $"v{info.Version} · {state}{engine}";

        IncompatibleReason = info.IncompatibleReason is null ? null : "Not loaded: " + info.IncompatibleReason;
        HealthSummary = health?.Summary;

        // Permissions are DECLARATIONS, not enforcement (see PluginPermissions). The wording is
        // "Declares:" rather than "Can only:" for exactly that reason — overstating it here would
        // be worse than showing nothing, because a user would trust a boundary that isn't there.
        IReadOnlyList<string> perms = info.Permissions ?? Array.Empty<string>();
        if (perms.Count > 0)
        {
            // Sensitive ones first so a truncated glance still shows the ones that matter.
            string[] ordered = perms
                .OrderByDescending(PluginPermissions.IsSensitive)
                .ThenBy(p => p, StringComparer.Ordinal)
                .ToArray();
            PermissionSummary = "Declares: " + string.Join(", ", ordered);
            PermissionDetail = string.Join(Environment.NewLine,
                ordered.Select(p => "• " + (PluginPermissions.Describe(p) ?? p)
                                  + (PluginPermissions.IsKnown(p) ? "" : "  (unrecognised by this build)")))
                + Environment.NewLine + Environment.NewLine
                + "Declared by the plugin author. Scrye does not currently enforce these.";
        }

        ReloadCommand = new RelayCommand(() => reload(Id));
        ToggleCommand = new RelayCommand(() => setEnabled(Id, !Loaded));
        RemoveCommand = new RelayCommand(() => remove(Id));
    }
}
