using System.Collections.ObjectModel;
using Scrye.Core.Model;

namespace Scrye.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<WorldViewModel> Worlds { get; } = new();
    public RelayCommand ConnectCommand { get; }

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

    public MainWindowViewModel() => ConnectCommand = new RelayCommand(Connect);

    private async void Connect()
    {
        if (string.IsNullOrWhiteSpace(Host) || !int.TryParse(Port, out int port))
            return;

        var vm = new WorldViewModel(new WorldProfile { Name = Host, Host = Host, Port = port, UseTls = UseTls, AcceptInvalidCertificates = UseTls, EnableMip = EnableMip });
        Worlds.Add(vm);
        Active = vm;
        try { await vm.ConnectAsync(); }
        catch (Exception ex) { vm.AppendSystem($"connect failed: {ex.Message}"); }
    }
}
