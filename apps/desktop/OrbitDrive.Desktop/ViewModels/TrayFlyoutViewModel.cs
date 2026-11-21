using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>What the tray flyout shows: the sharing state in plain words and three actions. It reads the agent's real state and holds nothing of its own.</summary>
internal sealed class TrayFlyoutViewModel : ObservableObject
{
    public TrayFlyoutViewModel(ConsoleStatus? status, Action open, Action togglePause, Action quit)
    {
        (Title, Detail) = status?.State switch
        {
            "online" => ("Sharing is on", status.Roots.Count == 1 ? "1 folder is shared." : $"{status.Roots.Count} folders are shared."),
            "paused" => ("Sharing is paused", "Your devices can't see anything until you resume."),
            "connecting" => ("Connecting…", "Trying to reach the OrbitDrive relay."),
            "update_required" => ("Update needed", "Open OrbitDrive and go to Settings to update."),
            "not_configured" => ("Not set up yet", "Open OrbitDrive to finish setting up."),
            null => ("OrbitDrive isn't running sharing", "The sharing program isn't reachable. Open OrbitDrive for details."),
            _ => ("OrbitDrive", ""),
        };
        CanTogglePause = status?.State is "online" or "paused";
        PauseText = status?.State == "paused" ? "Resume sharing" : "Pause sharing";
        OpenCommand = new RelayCommand(open);
        PauseCommand = new RelayCommand(togglePause);
        QuitCommand = new RelayCommand(quit);
    }

    public string Title { get; }
    public string Detail { get; }
    public string PauseText { get; }
    public bool CanTogglePause { get; }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand PauseCommand { get; }
    public IRelayCommand QuitCommand { get; }
}
