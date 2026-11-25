using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Input;
using OrbitDrive.Core.Ipc;
using OrbitDrive.Desktop.Controls;

namespace OrbitDrive.Desktop.ViewModels;

/// <summary>One shared folder as the page shows it. Built only from what the agent reported.</summary>
internal sealed record FolderItem(string Id, string Name, string Path, bool Available, bool ShowHidden, bool UploadsEnabled)
{
    public string PillText => !Available ? "Unavailable" : UploadsEnabled ? "Can add files" : "Read-only";
    public string PillGlyph => !Available ? "" : UploadsEnabled ? "" : "";
    public StatusKind PillKind => !Available || UploadsEnabled ? StatusKind.Warning : StatusKind.Good;
    public string Note => !Available
        ? "The folder is missing, or a different disk is at this location."
        : ShowHidden ? "Hidden items are included." : "Hidden items stay private.";

    /// <summary>What a screen reader announces for the whole card.</summary>
    public string AutomationName =>
        $"{Name}  —  {Path}  ({(UploadsEnabled ? "accepts uploads" : "read-only")}{(Available ? "" : ", currently unavailable")}{(ShowHidden ? ", hidden items included" : "")})";
}

/// <summary>
/// Shared folders. Cards for what is shared, one primary action (share a folder), actions for the selected folder, and a typed-path alternative.
/// Dialogs are passed in so this has no window dependencies.
/// </summary>
internal sealed class FoldersPageViewModel : PageViewModel
{
    private const int UploadQuotaMiB = 10240, UploadMaxFileMiB = 4096;
    private readonly ConsoleHost _host;
    private readonly Func<string?> _pickFolder;
    private readonly Func<string, string, bool> _confirm;
    private string _shown = "";
    private FolderItem? _selected;
    private string _newName = "", _newPath = "", _error = "";
    private bool _includeHidden;

    public FoldersPageViewModel(ConsoleHost host, Func<string?> pickFolder, Func<string, string, bool> confirm)
    {
        _host = host; _pickFolder = pickFolder; _confirm = confirm;
        ShareFolderCommand = new AsyncRelayCommand(ShareWithPickerAsync);
        AddByPathCommand = new AsyncRelayCommand(() => AddAsync(NewName.Trim(), NewPath.Trim(), IncludeHidden, clearForm: true));
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
        ToggleHiddenCommand = new AsyncRelayCommand(ToggleHiddenAsync);
        ToggleUploadsCommand = new AsyncRelayCommand(ToggleUploadsAsync);
        host.AddStatusConsumer(OnStatus);
    }

    public override string Title => PageNames.Folders;

    public IRelayCommand ShareFolderCommand { get; }
    public IRelayCommand AddByPathCommand { get; }
    public IRelayCommand RemoveCommand { get; }
    public IRelayCommand ToggleHiddenCommand { get; }
    public IRelayCommand ToggleUploadsCommand { get; }

    public ObservableCollection<FolderItem> Folders { get; } = [];
    public bool HasFolders => Folders.Count > 0;
    public bool NoFolders => Folders.Count == 0;

    public FolderItem? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedHeading));
            OnPropertyChanged(nameof(HiddenActionText)); OnPropertyChanged(nameof(UploadsActionText));
        }
    }

    public bool HasSelection => _selected is not null;
    public string SelectedHeading => _selected is null ? "" : $"Options for {_selected.Name}";
    public string HiddenActionText => _selected is { ShowHidden: true } ? "Stop sharing hidden items" : "Share hidden items too";
    public string UploadsActionText => _selected is { UploadsEnabled: true } ? "Stop accepting new files" : "Allow adding files";

    public string NewName { get => _newName; set => SetProperty(ref _newName, value); }
    public string NewPath { get => _newPath; set => SetProperty(ref _newPath, value); }
    public bool IncludeHidden { get => _includeHidden; set => SetProperty(ref _includeHidden, value); }
    public string Error { get => _error; private set { if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    /// <summary>Called by the view when a folder is dropped on the page.</summary>
    public Task ShareDroppedAsync(string path) => AddAsync(FolderName(path), path, false, clearForm: false);

    private static string FolderName(string path) => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;

    private async Task ShareWithPickerAsync()
    {
        if (_pickFolder() is not { Length: > 0 } path) return;
        await AddAsync(FolderName(path), path, false, clearForm: false);
    }

    private async Task AddAsync(string name, string path, bool showHidden, bool clearForm)
    {
        if (path.Length > 0 && Path.GetPathRoot(path) == path &&
            !_confirm("Share an entire drive", $"Share the whole drive {path}?\n\nEvery folder on it that your Windows account can read becomes visible to your paired devices."))
            return;
        var problem = await _host.RunAsync(client => client.SendAsync("add_root", new JsonObject { ["name"] = name, ["path"] = path, ["showHidden"] = showHidden }));
        Error = problem ?? "";
        if (problem is null && clearForm) { NewName = ""; NewPath = ""; IncludeHidden = false; }
    }

    private async Task RemoveAsync()
    {
        if (_selected is not { } folder) return;
        if (!_confirm("Stop sharing this folder", $"Stop sharing “{folder.Name}”?\n\nPaired devices will no longer see it. Nothing is deleted from this PC.")) return;
        Error = await _host.RunAsync(client => client.SendAsync("remove_root", new JsonObject { ["rootId"] = folder.Id })) ?? "";
    }

    private async Task ToggleHiddenAsync()
    {
        if (_selected is not { } folder) return;
        Error = await _host.RunAsync(client => client.SendAsync("set_root_hidden", new JsonObject { ["rootId"] = folder.Id, ["showHidden"] = !folder.ShowHidden })) ?? "";
    }

    private async Task ToggleUploadsAsync()
    {
        if (_selected is not { } folder) return;
        var turnOn = !folder.UploadsEnabled;
        if (turnOn && !_confirm("Allow adding files",
                "Paired phones and browsers will be able to ADD files to this folder (up to 10 GiB in total, 4 GiB per file). They can never replace, delete or run anything. Turn this on?")) return;
        Error = await _host.RunAsync(client => client.SendAsync("set_root_uploads", new JsonObject { ["rootId"] = folder.Id, ["enabled"] = turnOn, ["quotaMiB"] = UploadQuotaMiB, ["maxFileMiB"] = UploadMaxFileMiB })) ?? "";
    }

    private Task OnStatus(ConsoleStatus? status)
    {
        if (status is null) return Task.CompletedTask;   // agent not reachable: keep what was shown; Home explains why
        var key = string.Join("|", status.Roots.Select(r => $"{r.RootId};{r.Name};{r.Path};{r.Available};{r.ShowHidden};{r.UploadsEnabled}"));
        if (key == _shown) return Task.CompletedTask;
        _shown = key;
        var keep = _selected?.Id;
        Folders.Clear();
        foreach (var r in status.Roots) Folders.Add(new FolderItem(r.RootId, r.Name, r.Path, r.Available, r.ShowHidden, r.UploadsEnabled));
        Selected = keep is null ? null : Folders.FirstOrDefault(f => f.Id == keep);
        OnPropertyChanged(nameof(HasFolders)); OnPropertyChanged(nameof(NoFolders));
        return Task.CompletedTask;
    }
}
