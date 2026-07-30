using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = new();
    public RelayCommand RescanCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CloseCommand { get; }

    public PluginsViewModel(Func<IReadOnlyList<PluginInfo>> list,
                            Action<string, Action> reload,
                            Action<string, bool, Action> setEnabled,
                            Action<string, Action> remove,
                            Action<Action> rescan,
                            Action<Action> newPlugin,
                            Action openFolder)
    {
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
        foreach (PluginInfo p in _list())
            Plugins.Add(new PluginRowViewModel(p,
                id => _reload(id, Refresh),
                (id, enable) => _setEnabled(id, enable, Refresh),
                id => _remove(id, Refresh)));
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

    public RelayCommand ReloadCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public PluginRowViewModel(PluginInfo info, Action<string> reload, Action<string, bool> setEnabled, Action<string> remove)
    {
        Id = info.Id;
        Name = string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name;
        Loaded = info.Loaded;
        Removable = info.Removable;
        Detail = $"v{info.Version} · {(info.Loaded ? "loaded" : "disabled")}";
        ReloadCommand = new RelayCommand(() => reload(Id));
        ToggleCommand = new RelayCommand(() => setEnabled(Id, !Loaded));
        RemoveCommand = new RelayCommand(() => remove(Id));
    }
}
