using CommunityToolkit.Mvvm.ComponentModel;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>One page of the console. Pages show the agent's real state; they keep none of their own.</summary>
internal abstract class PageViewModel : ObservableObject
{
    private bool _active;

    public abstract string Title { get; }

    /// <summary>True while this page is the one on screen. Pages fetch extra data (such as history) only while active.</summary>
    public bool IsActive
    {
        get => _active;
        internal set { if (SetProperty(ref _active, value)) OnActiveChanged(value); }
    }

    protected virtual void OnActiveChanged(bool active) { }
}

/// <summary>One entry in the left navigation.</summary>
internal sealed class NavItem(string glyph, PageViewModel page, string shortcut)
{
    public string Glyph { get; } = glyph;
    public PageViewModel Page { get; } = page;
    public string Title => Page.Title;
    public string ToolTipText { get; } = $"{page.Title} ({shortcut})";
}
