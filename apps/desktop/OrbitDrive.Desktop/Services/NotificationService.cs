using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using OrbitDrive.Core;
using OrbitDrive.Desktop.ViewModels;

namespace OrbitDrive.Desktop;

/// <summary>
/// Windows toasts for the two things worth interrupting the owner for. A request to connect gets Deny and Review buttons and never Approve (the owner has to see the
/// confirmation code in the console first); a finished transfer is a plain notice. Nothing is shown while the console window is the active window. If a toast can't be
/// registered or shown (no Start-menu shortcut, notifications off, a server edition of Windows), a tray balloon says the same thing.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly ConsoleHost _host;
    private readonly Dispatcher _dispatcher;
    private readonly TrayService _tray;
    private readonly Func<bool> _windowActive;
    private readonly Action<string> _review;
    private readonly NoticeTracker _tracker = new(new NoticeThrottle(TimeSpan.FromSeconds(30), () => DateTimeOffset.UtcNow));
    private string? _toastCode;
    private bool _toastsBroken;

    /// <param name="review">Brings the console forward on the page with this title.</param>
    public NotificationService(ConsoleHost host, Dispatcher dispatcher, TrayService tray, Func<bool> windowActive, Action<string> review)
    {
        _host = host; _dispatcher = dispatcher; _tray = tray; _windowActive = windowActive; _review = review;
        try { ToastNotificationManagerCompat.OnActivated += OnActivated; }
        catch (Exception e) when (e is not OutOfMemoryException) { _toastsBroken = true; _host.Log("Windows notifications are unavailable; using the tray icon instead"); }
        host.AddStatusConsumer(OnStatus);
    }

    private Task OnStatus(ConsoleStatus? status)
    {
        if (status is null) return Task.CompletedTask;
        var transfers = status.Transfers.Select(t => ($"{t.Name};{t.Started.UtcTicks}", t.Outcome));
        foreach (var notice in _tracker.Observe(status.Approval?.ConfirmationCode, status.Approval?.BrowserName, transfers))
        {
            if (_windowActive()) continue;                    // the owner is looking at the console already
            if (notice.Kind == NoticeKind.PairingRequest) _toastCode = status.Approval?.ConfirmationCode;
            Show(notice);
        }
        return Task.CompletedTask;
    }

    private void Show(Notice notice)
    {
        var page = notice.Kind == NoticeKind.PairingRequest ? PageNames.Devices : PageNames.Activity;
        if (!_toastsBroken)
        {
            try
            {
                var builder = new ToastContentBuilder().AddText(notice.Title).AddText(notice.Body);
                if (notice.Actions.Contains(NoticeAction.Deny))
                    builder.AddButton(new ToastButton().SetContent("Deny").AddArgument("action", "deny").SetBackgroundActivation());
                if (notice.Actions.Contains(NoticeAction.Review))
                    builder.AddButton(new ToastButton().SetContent("Review").AddArgument("action", "review").AddArgument("page", page));
                builder.Show();
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _toastsBroken = true;
                _host.Log("A Windows notification could not be shown; using the tray icon instead");
            }
        }
        _tray.ShowBalloon(notice.Title, notice.Body, () => _review(page));
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        _ = _dispatcher.InvokeAsync(async () =>
        {
            if (args.TryGetValue("action", out var action) && action == "deny")
            {
                // Only the request the toast was about: a newer request needs its own decision.
                if (_toastCode is not null && _host.Status?.Approval?.ConfirmationCode == _toastCode)
                    await _host.RunAsync(client => client.SendAsync("deny"));
            }
            else _review(args.TryGetValue("page", out var page) ? page : PageNames.Devices);
        });
    }

    public void Dispose()
    {
        if (_toastsBroken) return;
        try { ToastNotificationManagerCompat.OnActivated -= OnActivated; ToastNotificationManagerCompat.History.Clear(); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}
