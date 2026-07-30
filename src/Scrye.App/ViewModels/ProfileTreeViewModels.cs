using System.Collections.ObjectModel;
using Scrye.Core.Profiles;

namespace Scrye.App.ViewModels;

/// <summary>
/// Identifies which layer chain a connected tab was resolved from, so edits to any
/// layer in that chain can be re-resolved and live-applied to the session.
/// Character null = connected to the account or bare MUD; Account null = no account layer.
/// </summary>
public sealed record ProfileRef(string Mud, string? Account, string? Character);

/// <summary>
/// One node in the sidebar profile tree: a MUD, an account under a MUD, or a
/// character (under an account, or directly under a MUD when the game has no
/// account concept). One class for all three keeps the TreeView to a single
/// template; <see cref="Kind"/> + the identity fields say what it is.
/// </summary>
public sealed class ProfileNodeViewModel : ViewModelBase
{
    public LayerKind Kind { get; }

    /// <summary>The MUD this node belongs to (its own name for MUD nodes).</summary>
    public string Mud { get; }

    /// <summary>For account nodes: the account's own name. For character nodes:
    /// the owning account, or null when the character hangs directly off the MUD.</summary>
    public string? Account { get; }

    public string Name { get; }

    public ObservableCollection<ProfileNodeViewModel> Children { get; } = new();

    public string Glyph => Kind switch
    {
        LayerKind.Mud => "🌐",
        LayerKind.Account => "👥",
        _ => "👤",
    };

    public ProfileNodeViewModel(LayerKind kind, string mud, string? account, string name)
    {
        Kind = kind;
        Mud = mud;
        Account = account;
        Name = name;
    }

    /// <summary>The layer chain this node connects/resolves as.</summary>
    public ProfileRef ToRef() => Kind switch
    {
        LayerKind.Character => new ProfileRef(Mud, Account, Name),
        LayerKind.Account => new ProfileRef(Mud, Name, null),
        _ => new ProfileRef(Mud, null, null),
    };
}
