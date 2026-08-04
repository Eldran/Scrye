using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Scrye.Core.Plugins;
using Scrye.Scripting.Plugins;

namespace Scrye.App.ViewModels;

/// <summary>Backs the per-world plugins-manager panel: lists discovered plugins and their
/// loaded/removable state, and offers reload / enable-disable / remove, plus add workflows
/// (create a starter plugin, open the plugins folder, rescan disk). The mutating actions are
/// routed (by the caller) onto the session loop; this VM refreshes from a snapshot afterward.</summary>
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
    public PluginsViewModel(Func<IReadOnlyList<PluginInfo>> list,
                            Action<string, Action> reload,
                            Action<string, bool, Action> setEnabled,
                            Action<string, Action> remove,
                            Action<Action> rescan,
                            Action<Action> newPlugin,
                            Action openFolder,
                            Func<IReadOnlyList<PluginHealth>>? health = null)
    {
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
    }
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
        Detail = info.RequiresApi is { Length: > 0 }
            ? $"v{info.Version} · {state} · needs API {info.RequiresApi}"
            : $"v{info.Version} · {state}";

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
