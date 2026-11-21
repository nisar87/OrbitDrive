using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>One page of the console. Pages show the agent's real state; they keep none of their own.</summary>
internal abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
}

/// <summary>TRANSITIONAL: hosts a page that has not been rebuilt yet (see <see cref="LegacyConsole"/>). Deleted with it.</summary>
internal sealed class LegacyPageViewModel(string title, FrameworkElement view) : PageViewModel
{
    public override string Title { get; } = title;
    public FrameworkElement View { get; } = view;
}

/// <summary>One entry in the left navigation.</summary>
internal sealed class NavItem(string glyph, PageViewModel page, string shortcut)
{
    public string Glyph { get; } = glyph;
    public PageViewModel Page { get; } = page;
    public string Title => Page.Title;
    public string ToolTipText { get; } = $"{page.Title} ({shortcut})";
}
