using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HupAddonManager.App.Services;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.SystemIntegration;
using HupAddonManager.Core.Wow;
using Microsoft.Win32;

namespace HupAddonManager.App.ViewModels;

public sealed record IntervalOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => _main.Manager.Settings;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _selectedInterval = Intervals.FirstOrDefault(i => i.Minutes == S.CheckIntervalMinutes) ?? Intervals[2];
    }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        _main.Manager.SaveSettings();
        OnPropertyChanged(name);
        SavedNote = "Saved ✓";
    }

    [ObservableProperty] private string _savedNote = "";
    [ObservableProperty] private string _appUpdateNote = "";

    public string WowRoot => S.WowRoot ?? "(not set)";

    public IReadOnlyList<IntervalOption> Intervals { get; } = new[]
    {
        new IntervalOption(0, "Off"), new IntervalOption(30, "Every 30 minutes"), new IntervalOption(60, "Every hour"),
        new IntervalOption(180, "Every 3 hours"), new IntervalOption(360, "Every 6 hours"), new IntervalOption(720, "Every 12 hours"),
    };

    [ObservableProperty] private IntervalOption _selectedInterval;
    partial void OnSelectedIntervalChanged(IntervalOption value)
    {
        S.CheckIntervalMinutes = value.Minutes;
        Save(nameof(SelectedInterval));
        App.Current?.AutoUpdater?.Reschedule();
    }

    public bool AutoUpdate { get => S.AutoUpdate; set { S.AutoUpdate = value; Save(); } }
    public bool BackupWtf { get => S.BackupWtfBeforeUpdate; set { S.BackupWtfBeforeUpdate = value; Save(); } }
    public bool CloseToTray { get => S.CloseToTray; set { S.CloseToTray = value; Save(); } }
    public bool ExitAfterBootUpdate { get => S.ExitAfterBootUpdate; set { S.ExitAfterBootUpdate = value; Save(); } }
    public bool ShowNotifications { get => S.ShowNotifications; set { S.ShowNotifications = value; Save(); } }
    public bool CheckForAppUpdates { get => S.CheckForAppUpdates; set { S.CheckForAppUpdates = value; Save(); } }
    public int AddonBackupsToKeep { get => S.AddonBackupsToKeep; set { S.AddonBackupsToKeep = Math.Clamp(value, 1, 50); Save(); } }
    public int WtfBackupsToKeep { get => S.WtfBackupsToKeep; set { S.WtfBackupsToKeep = Math.Clamp(value, 1, 50); Save(); } }
    public string RepoUrl => AppInfo.RepoUrl;
    public string VersionText => "Installed version: v" + AppInfo.VersionText;

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            try
            {
                if (!AppPaths.IsTestInstance) Autostart.Set(value);
                S.StartWithWindows = value;
                Save();
                Log.Info(value ? "Start with Windows enabled" : "Start with Windows disabled");
            }
            catch (Exception ex)
            {
                Dialogs.Error("Start with Windows", "Couldn't change the Windows startup entry: " + ex.Message);
                OnPropertyChanged();
            }
        }
    }

    public bool HasCurseForgeKey => !string.IsNullOrEmpty(S.CurseForgeApiKeyProtected);
    public bool NeedsCurseForgeKey => !HasCurseForgeKey;
    public bool HasWagoKey => !string.IsNullOrEmpty(S.WagoApiKeyProtected);
    public bool HasGitHubToken => !string.IsNullOrEmpty(S.GitHubTokenProtected);

    public void SaveKeys(string? curse, string? wago, string? github)
    {
        if (curse != null) S.CurseForgeApiKey = curse;
        if (wago != null) S.WagoApiKey = wago;
        if (github != null) S.GitHubToken = github;
        _main.Manager.SaveSettings();
        OnPropertyChanged(nameof(HasCurseForgeKey));
        OnPropertyChanged(nameof(NeedsCurseForgeKey));
        OnPropertyChanged(nameof(HasWagoKey));
        OnPropertyChanged(nameof(HasGitHubToken));
        SavedNote = "API keys saved ✓";
        Log.Info("API keys updated");
    }

    [RelayCommand]
    private void BrowseWow()
    {
        var dlg = new OpenFolderDialog { Title = "Select your World of Warcraft folder (the one that contains _forever_)" };
        if (!string.IsNullOrEmpty(S.WowRoot)) dlg.InitialDirectory = S.WowRoot;
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FolderName;
        for (var p = path; p != null; p = System.IO.Path.GetDirectoryName(p))
        {
            if (WowLocator.IsWowRoot(p)) { path = p; break; }
        }
        if (!_main.Manager.SetWowRoot(path))
        {
            Dialogs.Error("Not a WoW folder", "That folder doesn't look like a World of Warcraft installation. Pick the folder that contains _forever_ (or _retail_, _classic_ ...).");
            return;
        }
        AfterWowChanged();
    }

    [RelayCommand]
    private void DetectWow()
    {
        var roots = WowLocator.FindRoots();
        if (roots.Count == 0)
        {
            Dialogs.Info("Auto-detect", "No World of Warcraft folder was found. Use 'Browse...' to pick it.");
            return;
        }
        _main.Manager.SetWowRoot(roots[0]);
        AfterWowChanged();
        if (roots.Count > 1) Dialogs.Info("Auto-detect", "Found several WoW folders; using:\n" + roots[0] + "\n\nOthers:\n" + string.Join("\n", roots.Skip(1)));
    }

    private void AfterWowChanged()
    {
        OnPropertyChanged(nameof(WowRoot));
        _main.RefreshWow();
        _main.Manager.Scan();
        _main.Backups.Reload();
        _main.Search.FlavorChanged();
        _ = _main.CheckAllAsync();
    }

    [RelayCommand]
    private void OpenDataFolder() => Shell.Open(AppPaths.DataDir);

    [RelayCommand]
    private void OpenLog() => Shell.Open(Log.TodayFile);

    [RelayCommand]
    private void OpenUrl(string url) => Shell.Open(url);

    [RelayCommand]
    private async Task CheckAppUpdateAsync()
    {
        AppUpdateNote = "Checking...";
        try
        {
            var info = await SelfUpdater.CheckAsync();
            _main.AppUpdate = info;
            AppUpdateNote = info == null ? $"You have the latest version (v{AppInfo.VersionText})." : $"Version {info.Tag} is available - see the banner at the top.";
        }
        catch (Exception ex) { AppUpdateNote = "Check failed: " + ex.Message; }
    }
}
