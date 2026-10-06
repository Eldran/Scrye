using System.Collections.ObjectModel;
using Scrye.App.Companion;
using Scrye.Core.Automation;
using Scrye.Core.Model;
using Scrye.Core.Profiles;
using Scrye.Core.Updates;

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
    // Save and Done are the same operation with a different ending. Both forms hold a whole
    // page of settings, so saving one addition used to cost you the form: adding three triggers
    // meant opening it three times. Save now applies and stays; Done applies and closes (what
    // Save alone used to do); Cancel still leaves without writing anything.
    public RelayCommand SaveEditorCommand { get; }
    public RelayCommand DoneEditorCommand { get; }
    public RelayCommand CancelEditorCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand DoneSettingsCommand { get; }
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

    // ---- cross-world chat relay ---------------------------------------------
    //
    // A tell to a character on one MUD is easy to miss while you are playing another, so a
    // world that allows it (WorldProfile.RelayChannels) offers its chat lines here and they
    // are drawn in whichever tab is in FRONT. Deliberately one-way and read-only: a reply
    // typed into this tab goes to THIS world, and routing it anywhere else on the strength
    // of the last line you saw is how a private message ends up on the wrong MUD.
    //
    // Runs on the source world's session loop, not the UI thread. That is safe because the
    // whole handler is a reference read plus an enqueue onto a concurrent queue — the tab
    // paints it on its next flush like any other line.

    private void AttachRelay(WorldViewModel world) => world.ChannelRelayed += OnChannelRelayed;
    private void DetachRelay(WorldViewModel world) => world.ChannelRelayed -= OnChannelRelayed;

    private void OnChannelRelayed(WorldViewModel source, string channel, string text)
    {
        WorldViewModel? target = Active;
        if (target is null || ReferenceEquals(target, source)) return;   // you are already reading it
        target.ReceiveRelay(source.Title, channel, text);
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

    // ---- the sidebar ---------------------------------------------------------
    // The MUD list earns its width while you are setting characters up and stops earning it
    // once you are playing, so it folds away to the thin strip that carries its own chevron —
    // the control never vanishes with the panel it controls. Written straight to disk on every
    // toggle rather than on some later Save, because a panel you collapsed should still be
    // collapsed tomorrow without you having confirmed it anywhere.

    private readonly Services.UiState _ui = Services.UiStateStore.Load();

    public bool SidebarCollapsed
    {
        get => _ui.SidebarCollapsed;
        set
        {
            if (_ui.SidebarCollapsed == value) return;
            _ui.SidebarCollapsed = value;
            Services.UiStateStore.Save(_ui);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SidebarChevron));
            OnPropertyChanged(nameof(SidebarToggleTip));
        }
    }

    /// <summary>Points the way the click will move the panel: ‹ folds it away, › brings it back.</summary>
    public string SidebarChevron => SidebarCollapsed ? "›" : "‹";

    public string SidebarToggleTip => SidebarCollapsed ? "Show the MUD list" : "Hide the MUD list";

    private ProfileNodeViewModel? _selectedNode;
    public ProfileNodeViewModel? SelectedNode { get => _selectedNode; set => SetField(ref _selectedNode, value); }

    private WorldEditorViewModel? _editor;
    public WorldEditorViewModel? Editor { get => _editor; set => SetField(ref _editor, value); }

    private GlobalSettingsViewModel? _settings;
    public GlobalSettingsViewModel? Settings { get => _settings; set => SetField(ref _settings, value); }

    // ---- "a new Scrye is out" -------------------------------------------------------
    // Checked once, a few seconds after start, and again whenever the version line at the
    // bottom of the sidebar is clicked. A quiet check that fails (offline, GitHub down) says
    // nothing; a clicked one says why. Nothing is downloaded: the notice links to the release.

    private static readonly string RunningVersion = ReleaseInfo.CurrentVersion();

    /// <summary>"Scrye v1.9.1" - the sidebar's last line, and the button that checks now.</summary>
    public string VersionText { get; } = "Scrye v" + RunningVersion;

    private ReleaseInfo? _release;
    private bool _checkingUpdate;

    private bool _updateAvailable;
    /// <summary>A newer release is known and has not been dismissed.</summary>
    public bool UpdateAvailable { get => _updateAvailable; private set => SetField(ref _updateAvailable, value); }

    public string? UpdateText => _release is null ? null : $"Scrye v{_release.Version} is out — you have v{RunningVersion}";

    /// <summary>The release note (the tag's annotation), as the notice's tooltip.</summary>
    public string? UpdateNotes => _release?.Notes ?? "See the release page for what changed.";

    private string? _updateStatus;
    /// <summary>What a clicked check found, when it found no update ("up to date", or why not).</summary>
    public string? UpdateStatus { get => _updateStatus; private set => SetField(ref _updateStatus, value); }

    public RelayCommand CheckUpdatesCommand { get; private set; } = null!;
    public RelayCommand OpenReleaseCommand { get; private set; } = null!;
    public RelayCommand DismissUpdateCommand { get; private set; } = null!;
    public RelayCommand SkipUpdateCommand { get; private set; } = null!;

    private void InitUpdateCheck()
    {
        CheckUpdatesCommand = new RelayCommand(() => CheckForUpdate(manual: true));
        OpenReleaseCommand = new RelayCommand(OpenRelease);
        DismissUpdateCommand = new RelayCommand(() => UpdateAvailable = false);
        SkipUpdateCommand = new RelayCommand(() =>
        {
            if (_release is not null) { _ui.SkippedRelease = _release.Version; Services.UiStateStore.Save(_ui); }
            UpdateAvailable = false;
        });

        // not in the first seconds: startup has better things to do than wait on GitHub
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => { timer.Stop(); CheckForUpdate(manual: false); };
        timer.Start();
    }

    private async void CheckForUpdate(bool manual)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        if (manual) UpdateStatus = "Checking for a new release…";
        try
        {
            ReleaseInfo r = await Services.UpdateChecker.LatestAsync();
            bool newer = r.IsNewerThan(RunningVersion);
            // a skipped version stays quiet at startup; asking by hand shows it anyway
            if (newer && (manual || _ui.SkippedRelease != r.Version))
            {
                _release = r;
                OnPropertyChanged(nameof(UpdateText));
                OnPropertyChanged(nameof(UpdateNotes));
                UpdateAvailable = true;
                UpdateStatus = null;
            }
            else if (manual)
                UpdateStatus = $"Up to date — v{r.Version} is the newest release";
        }
        catch (System.Exception ex)
        {
            if (manual) UpdateStatus = "Update check failed: " + ex.Message;
        }
        finally { _checkingUpdate = false; }
    }

    // ---- "your plugins have updates" -------------------------------------------------
    // The catalogue is only read when someone opens it, so a plugin update published there
    // reached nobody who did not go looking (Joakim, 6 Oct 2026). A few seconds after start
    // Scrye reads it once and says, under the release notice, which plugins in use have a
    // newer version. It only tells: updating stays a click in the Plugins panel.

    private bool _pluginUpdatesAvailable;
    public bool PluginUpdatesAvailable
    {
        get => _pluginUpdatesAvailable;
        private set => SetField(ref _pluginUpdatesAvailable, value);
    }

    private string? _pluginUpdatesText;
    public string? PluginUpdatesText { get => _pluginUpdatesText; private set => SetField(ref _pluginUpdatesText, value); }

    private string? _pluginUpdatesTip;
    /// <summary>Every update, one per line, as the notice's tooltip.</summary>
    public string? PluginUpdatesTip { get => _pluginUpdatesTip; private set => SetField(ref _pluginUpdatesTip, value); }

    public RelayCommand ShowPluginUpdatesCommand { get; private set; } = null!;
    public RelayCommand DismissPluginUpdatesCommand { get; private set; } = null!;

    private void InitPluginUpdateCheck()
    {
        ShowPluginUpdatesCommand = new RelayCommand(ShowPluginUpdates);
        DismissPluginUpdatesCommand = new RelayCommand(() => PluginUpdatesAvailable = false);
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => { timer.Stop(); CheckPluginUpdates(); };
        timer.Start();
    }

    private async void CheckPluginUpdates()
    {
        try
        {
            string? extra = Scrye.Core.Plugins.PluginCatalog.NormaliseRoot(Services.PluginPreferences.ExtraRoot);
            string bundled = System.IO.Path.Combine(System.AppContext.BaseDirectory, "plugins");
            string user = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Scrye", "plugins");
            var pending = await System.Threading.Tasks.Task.Run(async () =>
            {
                var index = await Services.PluginCatalogClient.LoadAsync(Services.PluginPreferences.CatalogUrl, null);
                var installed = Scrye.Core.Plugins.PluginCatalog.DiscoverNewest(extra, bundled, user);
                return Scrye.Core.Plugins.CatalogUpdates.Find(index, installed, _store.AllEnabledPlugins(), extra);
            });
            if (pending.Count == 0) return;
            PluginUpdatesText = Scrye.Core.Plugins.CatalogUpdates.Describe(pending);
            PluginUpdatesTip = string.Join("\n", pending.Select(p =>
                $"{(string.IsNullOrWhiteSpace(p.Entry.Name) ? p.Entry.Id : p.Entry.Name)}: v{p.InstalledVersion} → v{p.Entry.Version}"))
                + "\n\nUpdate them under Plugins → Catalogue in a connected world.";
            PluginUpdatesAvailable = true;
        }
        catch (System.Exception)
        {
            // offline, GitHub down, a broken index: the notice is a courtesy - say nothing
        }
    }

    /// <summary>Open the active world's Plugins panel on its Catalogue page, where the
    /// Update buttons are. With no world connected there is nothing to update into yet,
    /// so the notice stays and says so.</summary>
    private void ShowPluginUpdates()
    {
        if (Active is null)
        {
            PluginUpdatesText = (PluginUpdatesText ?? "").Split(" — ")[0] + " — connect a world, then Plugins → Catalogue";
            return;
        }
        Active.Plugins.ShowCatalogue = true;
        Active.Plugins.Open();
        PluginUpdatesAvailable = false;
    }

    private void OpenRelease()
    {
        if (_release is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = _release.PageUrl, UseShellExecute = true });
        }
        catch (System.Exception ex) { UpdateStatus = "Could not open the browser: " + ex.Message + " — " + _release.PageUrl; }
    }

    public MainWindowViewModel()
    {
        string dir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Scrye", "profiles");
        _store = new ProfileStore(dir);

        // Restore the saved color scheme + ANSI palette before the window is shown.
        ProfileLayer startupGlobal;
        string? globalProblem = null;
        try { startupGlobal = _store.LoadGlobal(); }
        catch (System.Exception ex) when (ex is System.IO.InvalidDataException or System.IO.IOException)
        {
            // A damaged global.json must not stop the client starting: run on defaults and say
            // so (the toast is raised once the commands below exist).
            startupGlobal = new ProfileLayer { Kind = LayerKind.Global, Name = "global" };
            globalProblem = ex.Message;
            Services.CrashLog.Write("load global profile", ex);
        }
        Services.ThemeService.Apply(startupGlobal.Theme);
        Services.ThemeService.ApplyAnsiPalette(startupGlobal.AnsiPalette);
        Services.InputPreferences.KeepAfterSend = startupGlobal.KeepInputAfterSend ?? false;
        Services.PluginPreferences.ExtraRoot = startupGlobal.ExtraPluginRoot;

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
        SaveEditorCommand = new RelayCommand(() => SaveEditor(close: false));
        DoneEditorCommand = new RelayCommand(() => SaveEditor(close: true));
        CancelEditorCommand = new RelayCommand(() => Editor = null);
        ToggleSidebarCommand = new RelayCommand(() => SidebarCollapsed = !SidebarCollapsed);
        OpenSettingsCommand = new RelayCommand(() => Settings = new GlobalSettingsViewModel(_store.LoadGlobal()));
        SaveSettingsCommand = new RelayCommand(() => SaveSettings(close: false));
        DoneSettingsCommand = new RelayCommand(() => SaveSettings(close: true));
        CancelSettingsCommand = new RelayCommand(() => Settings = null);
        CloseWorldCommand = new RelayCommand<WorldViewModel>(CloseWorld);
        Companion = new CompanionController(this);
        InitUpdateCheck();
        InitPluginUpdateCheck();

        RefreshTree();
        if (globalProblem is not null)
            RaiseToast("Global settings not loaded", globalProblem + " — running on defaults until you save Settings.");
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

    /// <summary>Write the open profile editor to disk. <paramref name="close"/> false leaves the
    /// form up so you can keep editing — see <see cref="SaveEditorCommand"/>.</summary>
    private void SaveEditor(bool close)
    {
        if (Editor is null) return;
        string fallback = Editor.TargetKind switch
        {
            LayerKind.Account => "New Account",
            LayerKind.Character => "New Character",
            _ => "New MUD",
        };
        string name = string.IsNullOrWhiteSpace(Editor.Name) ? fallback : Editor.Name.Trim();
        // The name becomes a folder: "." or "a/b" would write outside the profile tree (and
        // deleting a character called "." deleted its whole MUD). ProfileStore refuses such
        // names; say why here, before anything is written, rather than crash on the throw.
        if (!ProfileStore.IsValidName(name, out string? why))
        {
            RaiseToast("Not saved", $"'{name}' can't be used as a name: {why}");
            return;
        }
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
            // Only record the reference if the secret really landed. On Linux the store can be
            // present but unwritable (keyring locked, or no desktop session), and a PasswordRef
            // pointing at nothing would fail at login with nothing on screen to explain it.
            if (CredentialStore.Save(key, Editor.Password))
                layer.PasswordRef = key;
            else
                RaiseToast("Password not saved",
                    CredentialStore.UnavailableReason ?? "the OS credential store refused the write "
                        + "(is your keyring unlocked?). You'll be asked for the password at login.");
        }

        try
        {
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
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.IO.IOException
                                              or System.UnauthorizedAccessException)
        {
            // a rename onto an existing folder, a locked file, a disk that said no
            RaiseToast("Not saved", ex.Message);
            return;
        }

        // Before anything can close: the form is still open on Save, and it must now describe
        // the layer that exists rather than the one that was being created. Without this a
        // second Save would re-run the rename from a stale OriginalName.
        Editor.MarkSaved(name);

        if (close) Editor = null;
        else RaiseToast("Saved", $"{name} saved. The form is still open — Done closes it.");
        RefreshTree();
        ReapplyToConnected();   // any layer in a connected tab's chain may have changed
    }

    /// <summary>Write the global settings. <paramref name="close"/> false leaves the form up —
    /// see <see cref="SaveSettingsCommand"/>. Nothing here carries identity the way the profile
    /// editor's name does, so a repeat save is simply a repeat write.</summary>
    private void SaveSettings(bool close)
    {
        if (Settings is null) return;
        ProfileLayer layer = Settings.ToLayer();
        _store.SaveGlobal(layer);
        Services.ThemeService.Apply(layer.Theme);   // scheme change takes effect immediately
        Services.ThemeService.ApplyAnsiPalette(layer.AnsiPalette);
        Services.InputPreferences.KeepAfterSend = layer.KeepInputAfterSend ?? false;
        Services.PluginPreferences.ExtraRoot = layer.ExtraPluginRoot;
        if (close) Settings = null;
        else RaiseToast("Saved", "Settings saved. The form is still open — Done closes it.");
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

    /// <summary>Merge a parsed MUSHclient import into the connected node's own layer and
    /// live-apply it, so imported rules work without a reconnect.</summary>
    private bool ImportRules(ProfileRef r, Scrye.Core.Automation.MushclientImport import)
    {
        bool ok = Services.CrashLog.Guard("ImportRules", () => SaveImport(r, import));
        if (!ok) RaiseToast("Import", "Couldn't save the imported rules (see logs).");
        return ok;
    }

    private void SaveImport(ProfileRef r, Scrye.Core.Automation.MushclientImport import)
    {
        ProfileLayer layer = OwnLayer(r);
        // By name, the same way the profile cascade merges layers -- so importing the same
        // file twice updates its rules instead of ending up with two of each.
        MergeByName(layer.Triggers, import.Triggers, t => t.Name);
        MergeByName(layer.Aliases, import.Aliases, a => a.Name);
        MergeByName(layer.Timers, import.Timers, t => t.Name);
        MergeByName(layer.Macros, import.Macros, m => m.Key);
        foreach (KeyValuePair<string, string> v in import.Variables) layer.Variables[v.Key] = v.Value;
        SaveOwnLayer(r, layer);
        ReapplyToConnected();
    }

    private static void MergeByName<T>(List<T> into, IEnumerable<T> incoming, Func<T, string> key)
    {
        foreach (T item in incoming)
        {
            int i = into.FindIndex(x => string.Equals(key(x), key(item), StringComparison.OrdinalIgnoreCase));
            if (i >= 0) into[i] = item; else into.Add(item);
        }
    }

    /// <summary>The connected node's OWN layer (not the resolved cascade), created empty if it
    /// does not exist yet. The same choice SavePluginChoice and SaveTriggerNotify make.</summary>
    private ProfileLayer OwnLayer(ProfileRef r) =>
        r.Character is not null
            ? _store.LoadCharacter(r.Mud, r.Account, r.Character)
              ?? new ProfileLayer { Kind = LayerKind.Character, Name = r.Character }
        : r.Account is not null
            ? _store.LoadAccount(r.Mud, r.Account)
              ?? new ProfileLayer { Kind = LayerKind.Account, Name = r.Account }
        : _store.LoadMud(r.Mud) ?? new ProfileLayer { Kind = LayerKind.Mud, Name = r.Mud };

    private void SaveOwnLayer(ProfileRef r, ProfileLayer layer)
    {
        if (r.Character is not null) _store.SaveCharacter(r.Mud, r.Account, r.Character, layer);
        else if (r.Account is not null) _store.SaveAccount(r.Mud, r.Account, layer);
        else _store.SaveMud(r.Mud, layer);
    }

    /// <summary>Write the idle guard's settings into the connected node's own layer, so the
    /// Idle menu's choices hold next time. On a character this overrides what the MUD or
    /// account layer says, which is what choosing it for this character means.</summary>
    private void PersistIdleGuard(ProfileRef r, bool on, int seconds, Scrye.Core.Session.IdleSource sources, bool hold)
    {
        if (!Services.CrashLog.Guard("PersistIdleGuard", () =>
            {
                ProfileLayer layer = OwnLayer(r);
                string src = Scrye.Core.Session.IdleSources.Format(sources);
                if (layer.IdleGuard == on && layer.IdleGuardSeconds == seconds && layer.IdleGuardSources == src
                    && layer.IdleGuardHoldPlugins == hold) return;
                layer.IdleGuard = on;
                layer.IdleGuardHoldPlugins = hold;
                layer.IdleGuardSeconds = seconds;
                layer.IdleGuardSources = src;
                SaveOwnLayer(r, layer);
            }))
            RaiseToast("Idle guard", "Couldn't save the idle guard settings (see logs).");
    }

    private void PersistTriggerNotify(ProfileRef r, TriggerDef def, bool notify)
    {
        if (!Services.CrashLog.Guard("PersistTriggerNotify", () => SaveTriggerNotify(r, def, notify)))
            RaiseToast("Notifications", $"Couldn't save the Notify change for '{def.Name}' (see logs).");
    }

    /// <summary>Write a trigger's Notify flag into the connected node's own layer.
    ///
    /// <para>Two cases. If the trigger already lives in THIS layer it is replaced in place, which
    /// works whether or not it has a name. If it was inherited from a shallower layer the change
    /// is stored as an overriding copy here — the cascade merges rules by name, so the copy shadows
    /// the original for this character only, and the shallower layer keeps working for everyone
    /// else. That merge key is also why an inherited UNNAMED trigger can't be edited from the
    /// panel at all: anonymous rules get a synthetic per-layer key, so nothing written here could
    /// ever line up with it. The panel refuses those rather than silently writing a duplicate.</para>
    /// </summary>
    private void SaveTriggerNotify(ProfileRef r, TriggerDef def, bool notify)
    {
        ProfileLayer layer;
        if (r.Character is not null)
            layer = _store.LoadCharacter(r.Mud, r.Account, r.Character)
                    ?? new ProfileLayer { Kind = LayerKind.Character, Name = r.Character };
        else if (r.Account is not null)
            layer = _store.LoadAccount(r.Mud, r.Account) ?? new ProfileLayer { Kind = LayerKind.Account, Name = r.Account };
        else
            layer = _store.LoadMud(r.Mud) ?? new ProfileLayer { Kind = LayerKind.Mud, Name = r.Mud };

        int at = layer.Triggers.FindIndex(t => t.Name == def.Name && t.Pattern == def.Pattern);
        if (at >= 0)
        {
            if (layer.Triggers[at].Notify == notify) return;      // nothing to write
            layer.Triggers[at] = layer.Triggers[at] with { Notify = notify };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(def.Name)) return;      // unnameable override; panel blocks this
            layer.Triggers.Add(def with { Notify = notify });
        }

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
        EffectiveProfile eff;
        WorldViewModel vm;
        // async void: anything thrown here would take the whole app down. Resolving a corrupt
        // profile, reading the credential store or building the world (plugins load in its
        // constructor) can all throw, so the setup is guarded and a failure becomes a toast.
        try
        {
            eff = Resolve(r);
            if (eff.PasswordRef is not null)   // inject the auto-login secret at runtime only
                eff.World.Password = CredentialStore.Load(eff.PasswordRef) ?? "";
            vm = new WorldViewModel(eff) { Ref = r, Broadcast = SendBroadcast, Toast = RaiseToast };
            vm.PersistPluginEnable = (id, enabled) => PersistPluginEnable(r, id, enabled);
            vm.PersistTriggerNotify = (def, notify) => PersistTriggerNotify(r, def, notify);
            vm.PersistIdleGuard = (on, secs, sources, hold) => PersistIdleGuard(r, on, secs, sources, hold);
            vm.ImportRules = import => ImportRules(r, import);
            vm.CompanionControl = Companion;   // lets `.companion` start/stop the server
            Worlds.Add(vm);
            Companion.Attach(vm);
            AttachRelay(vm);
            Active = vm;
        }
        catch (System.Exception ex)
        {
            Services.CrashLog.Write("ConnectNode", ex);
            RaiseToast("Connect", $"Couldn't open {r.Character ?? r.Account ?? r.Mud}: {ex.Message}");
            return;
        }
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
        DetachRelay(world);        // a closed world must not keep relaying into the survivors

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

    /// <summary>App exit: stop the companion server and dispose every open world, the same
    /// teardown closing a tab does — which is what flushes the session log's buffered tail,
    /// saves the GMCP shape memory and frees the plugin runtimes. Bounded by
    /// <paramref name="timeout"/> so a wedged plugin or socket cannot hold the app open; the
    /// window awaits this (never blocks on it) and then closes for real.</summary>
    public async System.Threading.Tasks.Task ShutdownAsync(TimeSpan timeout)
    {
        var work = new List<System.Threading.Tasks.Task>();
        // Companion first: StopAsync detaches every world (telling devices they are gone)
        // before the worlds themselves start coming down.
        try { work.Add(Companion.StopAsync()); }
        catch (Exception ex) { Services.CrashLog.Write("shutdown/companion", ex); }
        foreach (WorldViewModel w in new List<WorldViewModel>(Worlds))
        {
            DetachRelay(w);
            work.Add(DisposeWorldAsync(w));
        }
        System.Threading.Tasks.Task all = System.Threading.Tasks.Task.WhenAll(work);
        await System.Threading.Tasks.Task.WhenAny(all, System.Threading.Tasks.Task.Delay(timeout));
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
        AttachRelay(vm);
        Active = vm;
        try { await vm.ConnectAsync(); }
        catch (System.Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }
}
