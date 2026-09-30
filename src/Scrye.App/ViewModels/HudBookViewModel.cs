using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Scrye.App.ViewModels;

/// <summary>
/// A book of HUD panels: several plugin panels sharing ONE place on the HUD, drawn one at a
/// time, with a row of tabs to flip between them.
///
/// <para>Nothing about a plugin changes when its panel goes into a book. The panels stay
/// exactly what they were - the same view models, bound to the same state, updating while
/// they are not the page on show - and a book is only a decision about which of them to draw.
/// That is why a book holds panels rather than re-hosting them: every page is still an
/// ordinary item on the HUD canvas, with the hidden ones simply not drawn, and the page on
/// show carries the book's position, size and collapsed state.</para>
///
/// <para>A book remembers its pages by panel KEY (<c>pluginId|title</c>), whether or not they
/// are loaded right now. Disabling a plugin takes its page out of the tabs but not out of the
/// book, so turning it back on puts it back where it was. Only "take it out" (the ⤴ button)
/// forgets a page, and a book left with one page stops being a book.</para>
/// </summary>
public sealed class HudBookViewModel : ViewModelBase
{
    public string Id { get; }

    /// <summary>Every page, loaded or not, in tab order.</summary>
    public List<string> PageKeys { get; } = new();

    /// <summary>The page to show, by key. When it is not loaded, the first loaded page shows.</summary>
    public string? FrontKey { get; set; }

    // The book's own geometry, which the page on show wears. Width/height NaN = auto-size.
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double W { get; set; } = double.NaN;
    public double H { get; set; } = double.NaN;
    public bool Collapsed { get; set; }

    private readonly List<HudPanelViewModel> _loaded = new();   // pages on the HUD now, in tab order
    private HudPanelViewModel? _shown;

    /// <summary>The tabs above the page on show: one per LOADED page.</summary>
    public ObservableCollection<HudBookTabViewModel> Tabs { get; } = new();

    public int LoadedCount => _loaded.Count;
    public IReadOnlyList<HudPanelViewModel> Loaded => _loaded;

    /// <summary>Raised when the user flips a page (so the layout is saved).</summary>
    internal Action? Flipped;

    public HudBookViewModel(string id) => Id = id;

    /// <summary>The page drawn: the front page when it is loaded, else the first loaded one.</summary>
    public HudPanelViewModel? Shown =>
        _loaded.FirstOrDefault(p => p.Key == FrontKey) ?? _loaded.FirstOrDefault();

    /// <summary>A page that belongs to this book arrived on the HUD: it takes the book's place.</summary>
    public void Attach(HudPanelViewModel panel)
    {
        if (_loaded.Contains(panel)) return;
        if (!PageKeys.Contains(panel.Key)) PageKeys.Add(panel.Key);
        int at = PageKeys.IndexOf(panel.Key);
        int i = 0;
        while (i < _loaded.Count && PageKeys.IndexOf(_loaded[i].Key) < at) i++;
        _loaded.Insert(i, panel);
        panel.Book = this;
        Wear(panel);
        Refresh();
    }

    /// <summary>A page left the HUD (its plugin was disabled or reloaded) but stays in the book.</summary>
    public void Unload(HudPanelViewModel panel)
    {
        if (!_loaded.Remove(panel)) return;
        panel.Book = null;
        panel.IsShown = true;
        if (ReferenceEquals(_shown, panel)) _shown = null;
        Refresh();
    }

    /// <summary>A page is taken out of the book for good.</summary>
    public void Remove(HudPanelViewModel panel)
    {
        PageKeys.Remove(panel.Key);
        if (FrontKey == panel.Key) FrontKey = null;
        Unload(panel);
    }

    /// <summary>Show another page. It opens where the book is, the size the book is.</summary>
    public void Flip(HudPanelViewModel panel)
    {
        if (!_loaded.Contains(panel) || ReferenceEquals(Shown, panel)) return;
        FrontKey = panel.Key;
        Wear(panel);
        Refresh();
        Flipped?.Invoke();
    }

    /// <summary>Put the book's geometry on a page (its own place and size are the book's while
    /// it is in it).</summary>
    public void Wear(HudPanelViewModel panel)
    {
        if (!double.IsNaN(X)) panel.X = X;
        if (!double.IsNaN(Y)) panel.Y = Y;
        panel.UserWidth = W;
        panel.UserHeight = H;
        panel.IsCollapsed = Collapsed;
    }

    /// <summary>The page on show was dragged, resized or rolled up: that is now the book's.</summary>
    public void TakeFrom(HudPanelViewModel panel)
    {
        X = panel.X; Y = panel.Y; W = panel.UserWidth; H = panel.UserHeight;
        Collapsed = panel.IsCollapsed;
        foreach (HudPanelViewModel other in _loaded)
            if (!ReferenceEquals(other, panel)) Wear(other);
    }

    /// <summary>Recompute which page is drawn and rebuild the tabs.</summary>
    public void Refresh()
    {
        HudPanelViewModel? shown = Shown;
        foreach (HudPanelViewModel p in _loaded) p.IsShown = ReferenceEquals(p, shown);
        Tabs.Clear();
        foreach (HudPanelViewModel p in _loaded)
            Tabs.Add(new HudBookTabViewModel(p.Title, ReferenceEquals(p, shown), () => Flip(p)));
        foreach (HudPanelViewModel p in _loaded) p.RaiseBookChanged();
        if (!ReferenceEquals(shown, _shown))
        {
            _shown = shown;
            shown?.RequestPlace();         // the newly drawn page goes where the book is, on top
        }
    }
}

/// <summary>One tab of a book: a page's title; clicking it brings that page to the front.</summary>
public sealed class HudBookTabViewModel
{
    public string Title { get; }
    public bool IsFront { get; }
    public string Tip { get; }
    public RelayCommand FlipCommand { get; }

    public HudBookTabViewModel(string title, bool isFront, Action flip)
    {
        Title = title;
        IsFront = isFront;
        Tip = isFront ? title + " (showing)" : "Show " + title;
        FlipCommand = new RelayCommand(flip);
    }
}
