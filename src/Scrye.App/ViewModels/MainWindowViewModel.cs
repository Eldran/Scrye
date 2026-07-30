using System.Collections.ObjectModel;
using Scrye.Core.Model;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store;

    public ObservableCollection<WorldViewModel> Worlds { get; } = new();     // connected tabs
    public ObservableCollection<string> SavedWorlds { get; } = new();        // profile names

    public RelayCommand ConnectCommand { get; }         // quick-connect
    public RelayCommand NewWorldCommand { get; }
    public RelayCommand EditWorldCommand { get; }
    public RelayCommand DeleteWorldCommand { get; }
    public RelayCommand ConnectWorldCommand { get; }
    public RelayCommand SaveEditorCommand { get; }
    public RelayCommand CancelEditorCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand CancelSettingsCommand { get; }

    // quick-connect fields
    private string _host = "";
    public string Host { get => _host; set => SetField(ref _host, value); }
    private string _port = "23";
    public string Port { get => _port; set => SetField(ref _port, value); }
    private bool _useTls;
    public bool UseTls { get => _useTls; set => SetField(ref _useTls, value); }
    private bool _enableMip;
    public bool EnableMip { get => _enableMip; set => SetField(ref _enableMip, value); }

    private WorldViewModel? _active;
    public WorldViewModel? Active { get => _active; set => SetField(ref _active, value); }

    private string? _selectedWorld;
    public string? SelectedWorld { get => _selectedWorld; set => SetField(ref _selectedWorld, value); }

    private WorldEditorViewModel? _editor;
    public WorldEditorViewModel? Editor { get => _editor; set => SetField(ref _editor, value); }

    private GlobalSettingsViewModel? _settings;
    public GlobalSettingsViewModel? Settings { get => _settings; set => SetField(ref _settings, value); }

    public MainWindowViewModel()
    {
        string dir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Scrye", "profiles");
        _store = new ProfileStore(dir);

        ConnectCommand = new RelayCommand(QuickConnect);
        NewWorldCommand = new RelayCommand(() => Editor = new WorldEditorViewModel("New World", null, isNew: true));
        EditWorldCommand = new RelayCommand(EditWorld);
        DeleteWorldCommand = new RelayCommand(DeleteWorld);
        ConnectWorldCommand = new RelayCommand(ConnectWorld);
        SaveEditorCommand = new RelayCommand(SaveEditor);
        CancelEditorCommand = new RelayCommand(() => Editor = null);
        OpenSettingsCommand = new RelayCommand(() => Settings = new GlobalSettingsViewModel(_store.LoadGlobal()));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CancelSettingsCommand = new RelayCommand(() => Settings = null);

        RefreshWorlds();
    }

    private void RefreshWorlds()
    {
        SavedWorlds.Clear();
        foreach (string w in _store.ListWorlds()) SavedWorlds.Add(w);
    }

    private void EditWorld()
    {
        if (SelectedWorld is null) return;
        Editor = new WorldEditorViewModel(SelectedWorld, _store.LoadWorld(SelectedWorld), isNew: false);
    }

    private void DeleteWorld()
    {
        if (SelectedWorld is null) return;
        _store.DeleteWorld(SelectedWorld);
        RefreshWorlds();
    }

    private void SaveEditor()
    {
        if (Editor is null) return;
        string name = string.IsNullOrWhiteSpace(Editor.Name) ? "New World" : Editor.Name.Trim();
        ProfileLayer layer = Editor.ToLayer();
        if (!Editor.IsNew && !string.Equals(Editor.OriginalName, name, System.StringComparison.Ordinal))
            _store.DeleteWorld(Editor.OriginalName);
        _store.SaveWorld(name, layer);
        Editor = null;
        RefreshWorlds();
        SelectedWorld = name;
        ApplyRulesToConnected(name);   // live-apply to a connected tab of this world
    }

    private void SaveSettings()
    {
        if (Settings is null) return;
        _store.SaveGlobal(Settings.ToLayer());
        Settings = null;
        // global rules merge into every saved world: refresh each connected one
        foreach (WorldViewModel vm in Worlds)
            if (_store.LoadWorld(vm.Title) is not null)
                vm.ReloadRules(_store.ResolveWorld(vm.Title));
    }

    /// <summary>Push a saved world's freshly-resolved rules to any connected tab of it.</summary>
    private void ApplyRulesToConnected(string worldName)
    {
        if (_store.LoadWorld(worldName) is null) return;
        EffectiveProfile eff = _store.ResolveWorld(worldName);
        foreach (WorldViewModel vm in Worlds)
            if (string.Equals(vm.Title, worldName, System.StringComparison.Ordinal))
                vm.ReloadRules(eff);
    }

    private async void ConnectWorld()
    {
        if (SelectedWorld is null) return;
        EffectiveProfile eff = _store.ResolveWorld(SelectedWorld);
        var vm = new WorldViewModel(eff);
        Worlds.Add(vm);
        Active = vm;
        try { await vm.ConnectAsync(); }
        catch (System.Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }

    private async void QuickConnect()
    {
        if (string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out int port)) return;
        var vm = new WorldViewModel(new WorldProfile
        {
            Name = Host, Host = Host, Port = port,
            UseTls = UseTls, AcceptInvalidCertificates = UseTls, EnableMip = EnableMip,
        });
        Worlds.Add(vm);
        Active = vm;
        try { await vm.ConnectAsync(); }
        catch (System.Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }
}
