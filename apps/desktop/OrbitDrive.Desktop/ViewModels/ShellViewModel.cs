using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>The window's navigation: which pages exist, which one is showing, and whether the window is narrow enough to show icons only.</summary>
internal sealed class ShellViewModel : ObservableObject
{
    private NavItem? _selected;
    private bool _compact;

    public ShellViewModel(IEnumerable<NavItem> items)
    {
        NavItems = new ObservableCollection<NavItem>(items);
        _selected = NavItems.FirstOrDefault();
        if (_selected is not null) _selected.Page.IsActive = true;
    }

    public ObservableCollection<NavItem> NavItems { get; }

    public NavItem? SelectedNav
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!SetProperty(ref _selected, value)) return;
            if (previous is not null) previous.Page.IsActive = false;
            if (value is not null) value.Page.IsActive = true;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(SelectedTitle));
        }
    }

    public PageViewModel? CurrentPage => _selected?.Page;
    public string? SelectedTitle => _selected?.Title;

    /// <summary>A narrow window shows icons only, with smaller page margins.</summary>
    public bool IsCompact
    {
        get => _compact;
        set { if (SetProperty(ref _compact, value)) { OnPropertyChanged(nameof(NavColumnWidth)); OnPropertyChanged(nameof(PagePadding)); OnPropertyChanged(nameof(ShowLabels)); } }
    }

    public bool ShowLabels => !_compact;
    public GridLength NavColumnWidth => new((double)Application.Current.FindResource(_compact ? "Nav.WidthCompact" : "Nav.Width"));
    public Thickness PagePadding => (Thickness)Application.Current.FindResource(_compact ? "Inset.PageCompact" : "Inset.Page");

    /// <summary>Opens the page with this title (used by buttons on other pages, such as "Pair a device" on Home).</summary>
    public void GoTo(string title)
    {
        var target = NavItems.FirstOrDefault(item => item.Title == title);
        if (target is not null) SelectedNav = target;
    }

    public void SelectIndex(int index)
    {
        if (index >= 0 && index < NavItems.Count) SelectedNav = NavItems[index];
    }
}
