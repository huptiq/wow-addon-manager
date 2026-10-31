using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HupAddonManager.App.Services;
using HupAddonManager.App.Views;
using HupAddonManager.Core;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.SystemIntegration;
using HupAddonManager.Core.Wow;
using Microsoft.Win32;

namespace HupAddonManager.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private bool _suppressFlavorChange;

    public AddonManager Manager { get; }
    public SettingsViewModel Settings { get; }
    public BackupsViewModel Backups { get; }

    public ObservableCollection<AddonRowViewModel> Rows { get; } = new();
    public ICollectionView RowsView { get; }
    public ObservableCollection<WowFlavor> Flavors { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _progressVisible;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _updateCount;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private WowFlavor? _selectedFlavor;
    [ObservableProperty] private string _wowPathText = "";
    [ObservableProperty] private bool _hasWow;
    [ObservableProperty] private AppUpdateInfo? _appUpdate;
    [ObservableProperty] private AddonRowViewModel? _selectedRow;
    [ObservableProperty] private int _selectedTab;

    public string AppTitle => AppInfo.Name;
    public string AppVersion => "v" + AppInfo.VersionText;
    public bool HasAppUpdate => AppUpdate != null;
    public string AppUpdateText => AppUpdate == null ? "" : $"Version {AppUpdate.Tag} of {AppInfo.Name} is available.";
    public bool IsEmpty => HasWow && Rows.Count == 0;

    public MainViewModel(AddonManager manager)
    {
        Manager = manager;
        Settings = new SettingsViewModel(this);
        Backups = new BackupsViewModel(this);
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is AddonRowViewModel r && r.Matches(SearchText);

        manager.Changed += () => UI(RefreshRows);
        manager.Progress += (text, fraction) => UI(() => SetProgress(text, fraction));
        Log.Line += line => UI(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 600) LogLines.RemoveAt(0);
        });
        RefreshWow();
    }

    private static void UI(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    partial void OnSearchTextChanged(string value) => RowsView.Refresh();
    partial void OnSelectedTabChanged(int value) { if (value == 1) Backups.Reload(); }
    partial void OnAppUpdateChanged(AppUpdateInfo? value) { OnPropertyChanged(nameof(HasAppUpdate)); OnPropertyChanged(nameof(AppUpdateText)); }

    partial void OnSelectedFlavorChanged(WowFlavor? value)
    {
        if (_suppressFlavorChange || value == null || value.Key == Manager.Flavor?.Key) return;
        Manager.SelectFlavor(value.Key);
        WowPathText = value.AddOnsPath;
        Manager.Scan();
        Backups.Reload();
        _ = CheckAllAsync();
    }

    public void RefreshWow()
    {
        HasWow = Manager.Flavor != null;
        _suppressFlavorChange = true;
        Flavors.Clear();
        foreach (var f in Manager.Flavors) Flavors.Add(f);
        SelectedFlavor = Manager.Flavor;
        _suppressFlavorChange = false;
        WowPathText = Manager.Flavor?.AddOnsPath ?? "WoW folder not found - set it in Settings";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SetProgress(string text, double? fraction)
    {
        StatusText = text;
        IsBusy = Manager.IsBusy;
        if (text == "Ready" || !Manager.IsBusy && fraction == null)
        {
            ProgressVisible = false;
            return;
        }
        ProgressVisible = true;
        ProgressIndeterminate = fraction == null;
        ProgressValue = (fraction ?? 0) * 100;
    }

    public void RefreshRows()
    {
        var selectedKey = SelectedRow?.Key;
        var flavor = Manager.Flavor;
        Rows.Clear();
        foreach (var e in Manager.Entries)
        {
            var source = e.Tracked == null ? "-" : Manager.Providers.Get(e.Tracked.Provider)?.DisplayName ?? e.Tracked.Provider;
            Rows.Add(new AddonRowViewModel(e, source, flavor?.DisplayName ?? "this client", flavor?.InterfaceVersion));
        }
        UpdateCount = Rows.Count(r => r.Action == RowAction.Update);
        var unlinked = Rows.Count(r => r.Entry.Status == UpdateStatus.Unlinked);
        SummaryText = $"{Rows.Count} addon{(Rows.Count == 1 ? "" : "s")}"
                      + (UpdateCount > 0 ? $" · {UpdateCount} update{(UpdateCount == 1 ? "" : "s")}" : "")
                      + (unlinked > 0 ? $" · {unlinked} without source" : "");
        SelectedRow = Rows.FirstOrDefault(r => r.Key == selectedKey);
        IsBusy = Manager.IsBusy;
        OnPropertyChanged(nameof(IsEmpty));
        App.Current?.Tray?.SetUpdateCount(UpdateCount);
    }

    private async Task RunAsync(string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Log.Error(what + " failed", ex);
            Dialogs.Error(what + " failed", ex.Message);
        }
        finally { IsBusy = Manager.IsBusy; }
    }

    private bool EnsureNotBusy()
    {
        if (!Manager.IsBusy) return true;
        Dialogs.Info("Please wait", "Another operation is still running. Try again when it's finished.");
        return false;
    }

    [RelayCommand]
    private void AddAddon()
    {
        if (!HasWow) { SelectedTab = 2; return; }
        var w = Dialogs.ShowDialog(new AddAddonWindow(Manager));
        if (w.Result != null) Log.Info($"Added {w.Result.Name}");
    }

    [RelayCommand]
    public async Task CheckAllAsync()
    {
        if (!HasWow || Manager.IsBusy) return;
        await RunAsync("Update check", () => Manager.CheckAllAsync());
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (!HasWow || !EnsureNotBusy()) return;
        if (WowProcess.IsRunning(Manager.Flavor!.Path) &&
            !Dialogs.Confirm("WoW is running", $"{Manager.Flavor.DisplayName} is running. Updated addons are used after you reload the UI (/reload) or restart the game.\n\nUpdate now anyway?", "Update now"))
            return;

        await RunAsync("Update", async () =>
        {
            var s = await Manager.UpdateAllAsync(install: true);
            if (s.Failed.Count > 0)
                Dialogs.Error("Some updates failed", string.Join("\n", s.Failed.Select(f => $"• {f.Name}: {f.Error}")));
            else if (s.Updated.Count > 0)
                StatusText = $"Updated {s.Updated.Count} addon{(s.Updated.Count == 1 ? "" : "s")}";
            else
                StatusText = "Everything is up to date";
        });
    }

    [RelayCommand]
    private void ExportList()
    {
        if (!HasWow) return;
        var list = Manager.ExportList();
        if (list.Count == 0)
        {
            Dialogs.Info("Nothing to share", "No addons with a known source yet. Link or install addons first.");
            return;
        }
        var file = new ShareFile { Flavor = Manager.Flavor!.Key, Addons = list };
        var code = ShareCodec.ToCode(file);
        var w = new TextWindow("Share your addon list",
            $"{list.Count} addons. Send this code to a friend - they paste it in {AppInfo.Name} under Share -> Import list, and everything gets installed.",
            code, editable: false);
        w.AddButton("Save as file...", left: true, closes: false, onClick: _ =>
        {
            var dlg = new SaveFileDialog { Filter = "Addon list (*.hupaddons)|*.hupaddons", FileName = $"my-{Manager.Flavor.Key.Trim('_')}-addons.hupaddons" };
            if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, ShareCodec.ToJson(file));
        });
        w.AddButton("Copy code", primary: true, closes: false, onClick: _ => { Clipboard.SetText(code); StatusText = "Share code copied"; });
        w.AddButton("Close");
        Dialogs.ShowDialog(w);
    }

    [RelayCommand]
    private async Task ImportListAsync()
    {
        if (!HasWow || !EnsureNotBusy()) return;
        var w = new TextWindow("Import addon list",
            "Paste a share code (starts with HUP1:), or addon links - one per line. Addons you already have are skipped.",
            Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith(ShareCodec.Prefix) ? Clipboard.GetText().Trim() : "",
            editable: true);
        w.AddButton("Open file...", left: true, closes: false, onClick: tw =>
        {
            var dlg = new OpenFileDialog { Filter = "Addon list (*.hupaddons;*.json;*.txt)|*.hupaddons;*.json;*.txt|All files|*.*" };
            if (dlg.ShowDialog() == true) tw.SetText(File.ReadAllText(dlg.FileName));
        });
        w.AddButton("Cancel");
        w.AddButton("Install all", primary: true);
        Dialogs.ShowDialog(w);
        if (w.Result != "Install all") return;

        List<ShareEntry> entries;
        try { entries = ShareCodec.Parse(w.Text); }
        catch (Exception ex) { Dialogs.Error("Can't read that list", ex.Message); return; }
        if (entries.Count == 0) { Dialogs.Info("Nothing to import", "The list is empty."); return; }

        await RunAsync("Import", async () =>
        {
            var s = await Manager.ImportAsync(entries);
            var msg = $"Installed: {s.Updated.Count}";
            if (s.Failed.Count > 0) msg += "\n\nFailed:\n" + string.Join("\n", s.Failed.Select(f => $"• {f.Name}: {f.Error}"));
            Dialogs.Info("Import finished", msg);
        });
    }

    [RelayCommand]
    private async Task ImportWowUpAsync()
    {
        if (!HasWow || !EnsureNotBusy()) return;
        var n = Manager.ImportFromWowUp();
        if (n == 0)
        {
            Dialogs.Info("WowUp import", $"No new addon sources found in WowUp for {Manager.Flavor!.DisplayName}.");
            return;
        }
        Dialogs.Info("WowUp import", $"Took over the sources of {n} addon{(n == 1 ? "" : "s")} from WowUp. Checking for updates now.");
        await CheckAllAsync();
    }

    [RelayCommand]
    private void OpenAddOnsFolder()
    {
        if (Manager.Flavor != null) Shell.Open(Manager.Flavor.AddOnsPath);
    }

    [RelayCommand]
    private async Task ApplyAppUpdateAsync()
    {
        if (AppUpdate == null) return;
        await RunAsync("App update", async () =>
        {
            StatusText = $"Downloading {AppInfo.Name} {AppUpdate.Tag}...";
            ProgressVisible = true;
            var progress = new Progress<double>(p => ProgressValue = p * 100);
            if (await SelfUpdater.ApplyAsync(AppUpdate, progress))
                App.Current?.ExitApp();
            else
                Dialogs.Info("App update", "This copy can't update itself. Download the new version manually.");
        });
    }

    [RelayCommand]
    private async Task RowPrimaryAsync(AddonRowViewModel? row)
    {
        if (row == null) return;
        switch (row.Action)
        {
            case RowAction.Link: LinkRow(row); break;
            case RowAction.Update: case RowAction.Reinstall: await UpdateRowAsync(row); break;
            case RowAction.Retry: await CheckRowAsync(row); break;
        }
    }

    [RelayCommand]
    private async Task UpdateRowAsync(AddonRowViewModel? row)
    {
        if (row?.Tracked == null || !EnsureNotBusy()) return;
        await RunAsync($"Update of {row.Name}", () => Manager.UpdateAsync(row.Tracked));
    }

    [RelayCommand]
    private async Task CheckRowAsync(AddonRowViewModel? row)
    {
        if (row?.Tracked == null || !EnsureNotBusy()) return;
        await RunAsync($"Check of {row.Name}", () => Manager.CheckOneAsync(row.Tracked));
    }

    [RelayCommand]
    private void LinkRow(AddonRowViewModel? row)
    {
        if (row == null || !EnsureNotBusy()) return;
        Dialogs.ShowDialog(new AddAddonWindow(Manager, row.Entry, row.Tracked?.SourceUrl));
    }

    [RelayCommand]
    private void ShowChangelog(AddonRowViewModel? row)
    {
        var rel = row?.Tracked?.Latest;
        if (rel == null) { Dialogs.Info("Changelog", "Check for updates first."); return; }
        var w = new TextWindow($"{row!.Name} - {rel.Version}", rel.Date is { } d ? $"Released {d.LocalDateTime:g}" : null,
            string.IsNullOrWhiteSpace(rel.Changelog) ? "(The source didn't provide a changelog.)" : rel.Changelog!, editable: false);
        w.AddButton("Open website", left: true, closes: false, onClick: _ => OpenWebsite(row));
        w.AddButton("Close", primary: true);
        Dialogs.ShowDialog(w);
    }

    [RelayCommand]
    private void OpenFolder(AddonRowViewModel? row)
    {
        var folder = row?.Entry.FolderNames.FirstOrDefault();
        if (folder != null && Manager.Flavor != null)
        {
            var path = Path.Combine(Manager.Flavor.AddOnsPath, folder);
            Shell.Open(Directory.Exists(path) ? path : Manager.Flavor.AddOnsPath);
        }
    }

    [RelayCommand]
    private void OpenWebsite(AddonRowViewModel? row)
    {
        var t = row?.Tracked;
        if (t == null) return;
        var url = string.IsNullOrWhiteSpace(t.SourceUrl) ? Manager.Providers.Get(t.Provider)?.WebsiteFor(t.ProjectId) : t.SourceUrl;
        if (url != null) Shell.Open(url);
    }

    [RelayCommand]
    private void TogglePin(AddonRowViewModel? row)
    {
        if (row?.Tracked == null) return;
        Manager.SetPinned(row.Tracked, !row.Tracked.Pinned);
    }

    [RelayCommand]
    private async Task ToggleBetaAsync(AddonRowViewModel? row)
    {
        if (row?.Tracked == null || !EnsureNotBusy()) return;
        Manager.SetAllowPrerelease(row.Tracked, !row.Tracked.AllowPrerelease);
        await RunAsync($"Check of {row.Name}", () => Manager.CheckOneAsync(row.Tracked));
    }

    [RelayCommand]
    private void Unlink(AddonRowViewModel? row)
    {
        if (row?.Tracked == null) return;
        if (!Dialogs.Confirm("Unlink addon", $"Forget the source of '{row.Name}'? The addon stays installed but won't be updated anymore.", "Unlink")) return;
        Manager.Unlink(row.Tracked);
    }

    [RelayCommand]
    private async Task UninstallAsync(AddonRowViewModel? row)
    {
        if (row == null || !EnsureNotBusy()) return;
        if (!Dialogs.Confirm("Uninstall addon",
                $"Delete '{row.Name}' ({row.FoldersText}) from your AddOns folder?\n\nA backup is kept - you can restore it on the Backups tab.", "Uninstall"))
            return;
        await RunAsync($"Uninstall of {row.Name}", () => Manager.UninstallAsync(row.Entry));
        Backups.Reload();
    }
}
