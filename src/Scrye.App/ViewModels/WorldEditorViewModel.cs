using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>Backs the world settings form. Edits the scalar connection fields of a
/// world layer while preserving its triggers/aliases (edited elsewhere later).</summary>
public sealed class WorldEditorViewModel : ViewModelBase
{
    private readonly ProfileLayer _layer;

    public bool IsNew { get; }
    public string OriginalName { get; }
    public string Title => IsNew ? "New World" : "Edit World";

    private string _name;
    public string Name { get => _name; set => SetField(ref _name, value); }
    private string _host;
    public string Host { get => _host; set => SetField(ref _host, value); }
    private string _port;
    public string Port { get => _port; set => SetField(ref _port, value); }
    private string _username;
    public string Username { get => _username; set => SetField(ref _username, value); }
    private string _encoding;
    public string Encoding { get => _encoding; set => SetField(ref _encoding, value); }
    private bool _useTls;
    public bool UseTls { get => _useTls; set => SetField(ref _useTls, value); }
    private bool _enableMip;
    public bool EnableMip { get => _enableMip; set => SetField(ref _enableMip, value); }

    public WorldEditorViewModel(string name, ProfileLayer? layer, bool isNew)
    {
        IsNew = isNew;
        OriginalName = name;
        _layer = layer ?? new ProfileLayer { Kind = LayerKind.Mud };
        _name = name;
        _host = _layer.Host ?? "";
        _port = (_layer.Port ?? 23).ToString();
        _username = _layer.Username ?? "";
        _encoding = _layer.EncodingName ?? "utf-8";
        _useTls = _layer.UseTls ?? false;
        _enableMip = _layer.EnableMip ?? false;
    }

    /// <summary>Fold the edited fields back into the layer (keeps its collections).</summary>
    public ProfileLayer ToLayer()
    {
        int.TryParse(Port, out int port);
        _layer.Kind = LayerKind.Mud;
        _layer.Name = Name;
        _layer.Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim();
        _layer.Port = port > 0 ? port : 23;
        _layer.Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim();
        _layer.EncodingName = string.IsNullOrWhiteSpace(Encoding) ? null : Encoding.Trim();
        _layer.UseTls = UseTls ? true : null;
        _layer.AcceptInvalidCertificates = UseTls ? true : null;   // accept self-signed when TLS on
        _layer.EnableMip = EnableMip ? true : null;
        return _layer;
    }
}
