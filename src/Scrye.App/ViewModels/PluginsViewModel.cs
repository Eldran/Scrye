using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Scrye.Scripting.Plugins;

namespace Scrye.App.ViewModels;

/// <summary>Backs the per-world plugins-manager panel: lists discovered plugins and their
/// loaded state, and offers reload / enable-disable. All the mutating actions are routed
/// (by the caller) onto the session loop; this VM just refreshes from a snapshot afterward.</summary>
public sealed class PluginsViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PluginInfo>> _list;
    private readonly Action<string, Action> _reload;             // (id, onDone)
    private readonly Action<string, bool, Action> _setEnabled;   // (id, enable, onDone)

    public ObservableCollection<PluginRowViewModel> Plugins { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }

    public PluginsViewModel(Func<IReadOnlyList<PluginInfo>> list,
                            Action<string, Action> reload,
                            Action<string, bool, Action> setEnabled)
    {
        _list = list;
        _reload = reload;
        _setEnabled = setEnabled;
        RefreshCommand = new RelayCommand(Refresh);
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
                (id, enable) => _setEnabled(id, enable, Refresh)));
    }
}

/// <summary>One row in the plugins manager.</summary>
public sealed class PluginRowViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public bool Loaded { get; }
    public string Detail { get; }
    public string ToggleLabel => Loaded ? "Disable" : "Enable";

    public RelayCommand ReloadCommand { get; }
    public RelayCommand ToggleCommand { get; }

    public PluginRowViewModel(PluginInfo info, Action<string> reload, Action<string, bool> setEnabled)
    {
        Id = info.Id;
        Name = string.IsNullOrWhiteSpace(info.Name) ? info.Id : info.Name;
        Loaded = info.Loaded;
        Detail = $"v{info.Version} · {(info.Loaded ? "loaded" : "disabled")}";
        ReloadCommand = new RelayCommand(() => reload(Id));
        ToggleCommand = new RelayCommand(() => setEnabled(Id, !Loaded));
    }
}
