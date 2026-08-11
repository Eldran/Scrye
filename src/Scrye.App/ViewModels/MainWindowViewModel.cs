using System.Collections.ObjectModel;
using Scrye.App.Companion;
using Scrye.Core.Model;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly ProfileStore _store;

    public ObservableCollection<WorldViewModel> Worlds { get; } = new();          // connected tabs
    public ObservableCollection<ProfileNodeViewModel> Muds { get; } = new();      // sidebar tree roots

    /// <summary>Mobile companion server. Not started at launch — an idle client should not
    /// be listening on a socket the user did not ask for (companion design §7).</summary>
    public CompanionController Companion { get; }

    public RelayCommand ConnectCommand { get; }         // quick-connect
    public RelayCommand OpenQuickConnectCommand { get; }
    public RelayCommand CancelQuickConnectCommand { get; }

    private bool _quickConnectOpen;
    /// <summary>The quick-connect dialog (host/port/TLS/MIP, session-only) — the old
    /// always-visible top bar, now summoned on demand from the sidebar.</summary>
    public bool QuickConnectOpen { get => _quickConnectOpen; set => SetField(ref _quickConnectOpen, value); }
    public RelayCommand NewMudCommand { get; }
    public RelayCommand AddAccountCommand { get; }
    public RelayCommand AddCharacterCommand { get; }
    public RelayCommand EditNodeCommand { get; }
    public RelayCommand DeleteNodeCommand { get; }
    public RelayCommand ConnectNodeCommand { get; }
    public RelayCommand SaveEditorCommand { get; }
    public RelayCommand CancelEditorCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand CancelSettingsCommand { get; }
    public RelayCommand<WorldViewModel> CloseWorldCommand { get; }   // ✕ on a world tab

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
    public WorldViewModel? Active
    {
        get => _active;
        set
        {
            if (!SetField(ref _active, value)) return;
            foreach (WorldViewModel w in Worlds)          // tab badges track the visible tab
                w.IsActive = ReferenceEquals(w, value);
        }
    }

    /// <summary>Input-broadcast: deliver a command to every connected world tab.</summary>
    private void SendBroadcast(string text)
    {
        foreach (WorldViewModel w in Worlds) w.ReceiveBroadcast(text);
    }

    // ---- toast stack (trigger notifications + connection changes) -------------

    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    /// <summary>Raised after a toast is added — the window flashes the taskbar
    /// when it isn't focused.</summary>
    public event System.Action? ToastRaised;

    /// <summary>Add a toast (UI thread) and auto-expire it after ~6 seconds.</summary>
    public void RaiseToast(string title, string body)
    {
        var toast = new ToastViewModel(title, body);
        Toasts.Add(toast);
        while (Toasts.Count > 5) Toasts.RemoveAt(0);   // keep the stack short
        ToastRaised?.Invoke();

        var timer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) => { timer.Stop(); Toasts.Remove(toast); };
        timer.Start();
    }

    public void DismissToast(ToastViewModel? toast)
    {
        if (toast is not null) Toasts.Remove(toast);
    }

    private ProfileNodeViewModel? _selectedNode;
    public ProfileNodeViewModel? SelectedNode { get => _selectedNode; set => SetField(ref _selectedNode, value); }

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

        // Restore the saved color scheme + ANSI palette before the window is shown.
        ProfileLayer startupGlobal = _store.LoadGlobal();
        Services.ThemeService.Apply(startupGlobal.Theme);
        Services.ThemeService.ApplyAnsiPalette(startupGlobal.AnsiPalette);

        ConnectCommand = new RelayCommand(() => { QuickConnectOpen = false; QuickConnect(); });
        OpenQuickConnectCommand = new RelayCommand(() => QuickConnectOpen = true);
        CancelQuickConnectCommand = new RelayCommand(() => QuickConnectOpen = false);
        NewMudCommand = new RelayCommand(() =>
            Editor = new WorldEditorViewModel("New MUD", null, isNew: true, LayerKind.Mud));
        AddAccountCommand = new RelayCommand(AddAccount);
        AddCharacterCommand = new RelayCommand(AddCharacter);
        EditNodeCommand = new RelayCommand(EditNode);
        DeleteNodeCommand = new RelayCommand(DeleteNode);
        ConnectNodeCommand = new RelayCommand(ConnectNode);
        SaveEditorCommand = new RelayCommand(SaveEditor);
        CancelEditorCommand = new RelayCommand(() => Editor = null);
        OpenSettingsCommand = new RelayCommand(() => Settings = new GlobalSettingsViewModel(_store.LoadGlobal()));
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        CancelSettingsCommand = new RelayCommand(() => Settings = null);
        CloseWorldCommand = new RelayCommand<WorldViewModel>(CloseWorld);
        Companion = new CompanionController(this);

        RefreshTree();
    }

    // ---- sidebar tree --------------------------------------------------------

    private void RefreshTree()
    {
        Muds.Clear();
        foreach (string mud in _store.ListMuds())
        {
            var mudNode = new ProfileNodeViewModel(LayerKind.Mud, mud, null, mud);
            foreach (string account in _store.ListAccounts(mud))
            {
                var acctNode = new ProfileNodeViewModel(LayerKind.Account, mud, account, account);
                foreach (string ch in _store.ListCharacters(mud, account))
                    acctNode.Children.Add(new ProfileNodeViewModel(LayerKind.Character, mud, account, ch));
                mudNode.Children.Add(acctNode);
            }
            foreach (string ch in _store.ListCharacters(mud))   // account-less characters
                mudNode.Children.Add(new ProfileNodeViewModel(LayerKind.Character, mud, null, ch));
            Muds.Add(mudNode);
        }
    }

    private void AddAccount()
    {
        if (SelectedNode is null) return;   // any node identifies its MUD
        Editor = new WorldEditorViewModel("New Account", null, isNew: true,
                                          LayerKind.Account, parentMud: SelectedNode.Mud);
    }

    private void AddCharacter()
    {
        if (SelectedNode is null) return;
        // Under the selected account; as a sibling for a selected character; directly on a MUD.
        string? account = SelectedNode.Kind switch
        {
            LayerKind.Account => SelectedNode.Name,
            LayerKind.Character => SelectedNode.Account,
            _ => null,
        };
        Editor = new WorldEditorViewModel("New Character", null, isNew: true,
                                          LayerKind.Character, parentMud: SelectedNode.Mud, parentAccount: account);
    }

    private void EditNode()
    {
        if (SelectedNode is not ProfileNodeViewModel n) return;
        Editor = n.Kind switch
        {
            LayerKind.Mud => new WorldEditorViewModel(n.Name, _store.LoadMud(n.Name), isNew: false, LayerKind.Mud),
            LayerKind.Account => new WorldEditorViewModel(n.Name, _store.LoadAccount(n.Mud, n.Name), isNew: false,
                                                          LayerKind.Account, parentMud: n.Mud),
            _ => new WorldEditorViewModel(n.Name, _store.LoadCharacter(n.Mud, n.Account, n.Name), isNew: false,
                                          LayerKind.Character, parentMud: n.Mud, parentAccount: n.Account),
        };
    }

    private void DeleteNode()
    {
        if (SelectedNode is not ProfileNodeViewModel n) return;
        switch (n.Kind)
        {
            case LayerKind.Mud: _store.DeleteMud(n.Name); break;
            case LayerKind.Account: _store.DeleteAccount(n.Mud, n.Name); break;
            default: _store.DeleteCharacter(n.Mud, n.Account, n.Name); break;
        }
        SelectedNode = null;
        RefreshTree();
    }

    private void SaveEditor()
    {
        if (Editor is null) return;
        string fallback = Editor.TargetKind switch
        {
            LayerKind.Account => "New Account",
            LayerKind.Character => "New Character",
            _ => "New MUD",
        };
        string name = string.IsNullOrWhiteSpace(Editor.Name) ? fallback : Editor.Name.Trim();
        ProfileLayer layer = Editor.ToLayer();
        bool renamed = !Editor.IsNew && !string.Equals(Editor.OriginalName, name, System.StringComparison.Ordinal);

        // A typed password goes to the OS credential store; the layer only keeps the key.
        if (!string.IsNullOrEmpty(Editor.Password) && CredentialStore.Available)
        {
            string mudPart = Editor.ParentMud ?? name;
            string key = Editor.TargetKind switch
            {
                LayerKind.Mud => $"Scrye/mud/{name}",
                LayerKind.Account => $"Scrye/account/{mudPart}/{name}",
                _ => Editor.ParentAccount is null
                    ? $"Scrye/character/{mudPart}/{name}"
                    : $"Scrye/character/{mudPart}/{Editor.ParentAccount}/{name}",
            };
            CredentialStore.Save(key, Editor.Password);
            layer.PasswordRef = key;
        }

        switch (Editor.TargetKind)
        {
            case LayerKind.Mud:
                if (renamed) _store.RenameMud(Editor.OriginalName, name);   // accounts/chars move with it
                _store.SaveMud(name, layer);
                break;
            case LayerKind.Account:
                if (renamed) _store.RenameAccount(Editor.ParentMud!, Editor.OriginalName, name);
                _store.SaveAccount(Editor.ParentMud!, name, layer);
                break;
            default:
                if (renamed) _store.RenameCharacter(Editor.ParentMud!, Editor.ParentAccount, Editor.OriginalName, name);
                _store.SaveCharacter(Editor.ParentMud!, Editor.ParentAccount, name, layer);
                break;
        }

        Editor = null;
        RefreshTree();
        ReapplyToConnected();   // any layer in a connected tab's chain may have changed
    }

    private void SaveSettings()
    {
        if (Settings is null) return;
        ProfileLayer layer = Settings.ToLayer();
        _store.SaveGlobal(layer);
        Services.ThemeService.Apply(layer.Theme);   // scheme change takes effect immediately
        Services.ThemeService.ApplyAnsiPalette(layer.AnsiPalette);
        Settings = null;
        ReapplyToConnected();   // global merges into every chain
    }

    // ---- connecting ----------------------------------------------------------

    /// <summary>Save a plugin opt-in choice to the profile layer of the node the world was
    /// connected as (character, else account, else MUD), so it sticks to that character and
    /// doesn't leak to siblings. Runs on the UI thread.</summary>
    private void PersistPluginEnable(ProfileRef r, string id, bool enabled)
    {
        // never let a profile-save mishap crash the client (runs on the UI thread)
        if (!Services.CrashLog.Guard("PersistPluginEnable", () => SavePluginChoice(r, id, enabled)))
            RaiseToast("Plugins", $"Couldn't save the plugin choice for '{id}' (see logs).");
    }

    private void SavePluginChoice(ProfileRef r, string id, bool enabled)
    {
        // load the connected node's own layer (create an empty one if it doesn't exist yet)
        ProfileLayer layer;
        if (r.Character is not null)
            layer = _store.LoadCharacter(r.Mud, r.Account, r.Character)
                    ?? new ProfileLayer { Kind = LayerKind.Character, Name = r.Character };
        else if (r.Account is not null)
            layer = _store.LoadAccount(r.Mud, r.Account) ?? new ProfileLayer { Kind = LayerKind.Account, Name = r.Account };
        else
            layer = _store.LoadMud(r.Mud) ?? new ProfileLayer { Kind = LayerKind.Mud, Name = r.Mud };

        bool changed;
        if (enabled)
        {
            changed = !layer.Plugins.Contains(id);
            if (changed) layer.Plugins.Add(id);
        }
        else
        {
            changed = layer.Plugins.RemoveAll(p => p == id) > 0;
        }
        if (!changed) return;

        if (r.Character is not null) _store.SaveCharacter(r.Mud, r.Account, r.Character, layer);
        else if (r.Account is not null) _store.SaveAccount(r.Mud, r.Account, layer);
        else _store.SaveMud(r.Mud, layer);
    }

    private EffectiveProfile Resolve(ProfileRef r) =>
        r.Character is not null ? _store.ResolveCharacter(r.Mud, r.Account, r.Character)
        : r.Account is not null ? _store.ResolveAccount(r.Mud, r.Account)
        : _store.ResolveMud(r.Mud);

    /// <summary>Re-resolve every connected tab's layer chain and live-apply the rules.</summary>
    private void ReapplyToConnected()
    {
        foreach (WorldViewModel vm in Worlds)
        {
            if (vm.Ref is not ProfileRef r) continue;       // quick-connect tabs have no chain
            if (_store.LoadMud(r.Mud) is null) continue;    // its MUD was deleted — leave the session as-is
            vm.ReloadRules(Resolve(r));
        }
    }

    private async void ConnectNode()
    {
        if (SelectedNode is not ProfileNodeViewModel n) return;
        ProfileRef r = n.ToRef();
        EffectiveProfile eff = Resolve(r);
        if (eff.PasswordRef is not null)   // inject the auto-login secret at runtime only
            eff.World.Password = CredentialStore.Load(eff.PasswordRef) ?? "";
        var vm = new WorldViewModel(eff) { Ref = r, Broadcast = SendBroadcast, Toast = RaiseToast };
        vm.PersistPluginEnable = (id, enabled) => PersistPluginEnable(r, id, enabled);
        vm.CompanionControl = Companion;   // lets `.companion` start/stop the server
        Worlds.Add(vm);
        Companion.Attach(vm);
        Active = vm;
        if (string.IsNullOrEmpty(eff.World.Host))
        {
            vm.AppendSystem("no host set — add one on the MUD layer (Edit the MUD).");
            return;
        }
        try { await vm.ConnectAsync(); }
        catch (System.Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }

    /// <summary>Close a world tab (the ✕ on its header): pick an adjacent tab to fall
    /// back to, drop it from the list, then dispose it — which disconnects the session
    /// and tears down its plugins, HUD, capture panes and float windows.</summary>
    private void CloseWorld(WorldViewModel world)
    {
        int idx = Worlds.IndexOf(world);
        if (idx < 0) return;

        Companion.Detach(world);   // stop publishing and tell devices the session is gone

        if (ReferenceEquals(Active, world))               // choose a neighbour before removal
            Active = Worlds.Count > 1
                ? Worlds[idx == Worlds.Count - 1 ? idx - 1 : idx + 1]
                : null;

        Worlds.Remove(world);
        _ = DisposeWorldAsync(world);                     // fire-and-forget: closes the socket + cleans up
    }

    private static async System.Threading.Tasks.Task DisposeWorldAsync(WorldViewModel world)
    {
        try { await world.DisposeAsync(); }
        catch { /* teardown is best-effort; the tab is already gone */ }
    }

    private async void QuickConnect()
    {
        if (string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out int port)) return;
        var vm = new WorldViewModel(new WorldProfile
        {
            Name = Host, Host = Host, Port = port,
            UseTls = UseTls, AcceptInvalidCertificates = UseTls, EnableMip = EnableMip,
        })
        { Broadcast = SendBroadcast, Toast = RaiseToast };
        vm.CompanionControl = Companion;   // lets `.companion` start/stop the server
        Worlds.Add(vm);
        Companion.Attach(vm);
        Active = vm;
        try { await vm.ConnectAsync(); }
        catch (System.Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }
}
