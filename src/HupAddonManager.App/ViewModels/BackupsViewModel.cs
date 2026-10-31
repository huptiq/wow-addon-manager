using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HupAddonManager.App.Services;
using HupAddonManager.Core.Install;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.SystemIntegration;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.App.ViewModels;

public sealed class BackupRow
{
    public required AddonBackupInfo Info { get; init; }
    public string Name => Info.AddonName;
    public string Version => string.IsNullOrWhiteSpace(Info.Version) ? "unknown version" : Info.Version!;
    public string When => Info.Date.ToString("g");
    public string Size => BackupsViewModel.FormatSize(Info.Size);
    public string Folders => string.Join(", ", Info.Folders);
}

public sealed class WtfRow
{
    public required WtfBackupInfo Info { get; init; }
    public string When => $"{Info.Date:dddd}, {Info.Date:g}";
    public string Size => BackupsViewModel.FormatSize(Info.Size);
}

public sealed partial class BackupsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ObservableCollection<BackupRow> AddonBackups { get; } = new();
    public ObservableCollection<WtfRow> WtfBackups { get; } = new();
    [ObservableProperty] private bool _working;

    public BackupsViewModel(MainViewModel main) => _main = main;

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public void Reload()
    {
        AddonBackups.Clear();
        WtfBackups.Clear();
        var flavor = _main.Manager.Flavor;
        if (flavor == null) return;
        try
        {
            foreach (var b in BackupService.ListAddonBackups(flavor.Key)) AddonBackups.Add(new BackupRow { Info = b });
            foreach (var w in BackupService.ListWtfBackups(flavor.Key)) WtfBackups.Add(new WtfRow { Info = w });
        }
        catch (Exception ex) { Log.Error("Reading backups failed", ex); }
    }

    [RelayCommand]
    private async Task RestoreAddonAsync(BackupRow? row)
    {
        if (row == null || _main.Manager.IsBusy) return;
        if (!Dialogs.Confirm("Restore backup",
                $"Restore {row.Name} {row.Version} from {row.When}?\n\nThe current files are backed up first. The addon is then pinned so auto-update doesn't replace it again - unpin it in the ⋯ menu when you want updates back.",
                "Restore"))
            return;
        try
        {
            Working = true;
            await _main.Manager.RestoreBackupAsync(row.Info);
            Dialogs.Info("Restored", $"{row.Name} {row.Version} is back.");
        }
        catch (Exception ex) { Dialogs.Error("Restore failed", ex.Message); }
        finally { Working = false; Reload(); }
    }

    [RelayCommand]
    private void DeleteAddonBackup(BackupRow? row)
    {
        if (row == null) return;
        BackupService.Delete(row.Info.Path);
        Reload();
    }

    [RelayCommand]
    private async Task BackupWtfNowAsync()
    {
        var flavor = _main.Manager.Flavor;
        if (flavor == null) return;
        try
        {
            Working = true;
            _main.StatusText = "Backing up WTF...";
            var file = await Task.Run(() => BackupService.BackupWtf(flavor, _main.Manager.Settings.WtfBackupsToKeep, "manual"));
            _main.StatusText = file == null ? "No WTF folder found" : "WTF backup created";
        }
        catch (Exception ex) { Dialogs.Error("Backup failed", ex.Message); }
        finally { Working = false; Reload(); }
    }

    [RelayCommand]
    private async Task RestoreWtfAsync(WtfRow? row)
    {
        var flavor = _main.Manager.Flavor;
        if (row == null || flavor == null) return;
        if (WowProcess.IsRunning(flavor.Path))
        {
            Dialogs.Info("Close WoW first", "WoW overwrites the WTF folder when you log out, so restoring now would be undone. Close the game and try again.");
            return;
        }
        if (!Dialogs.Confirm("Restore addon settings",
                $"Restore the WTF folder (all addon settings, keybinds and macros) from {row.When}?\n\nYour current WTF folder is backed up first.", "Restore"))
            return;
        try
        {
            Working = true;
            await Task.Run(() => BackupService.RestoreWtf(flavor, row.Info, _main.Manager.Settings.WtfBackupsToKeep));
            Dialogs.Info("Restored", "Addon settings restored.");
        }
        catch (Exception ex) { Dialogs.Error("Restore failed", ex.Message); }
        finally { Working = false; Reload(); }
    }

    [RelayCommand]
    private void DeleteWtfBackup(WtfRow? row)
    {
        if (row == null) return;
        BackupService.Delete(row.Info.Path);
        Reload();
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var flavor = _main.Manager.Flavor;
        Shell.Open(flavor == null ? AppPaths.BackupsDir : BackupService.FlavorDir(flavor.Key));
    }
}
