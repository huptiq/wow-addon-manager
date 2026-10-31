using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows.Threading;
using HupAddonManager.Core;
using HupAddonManager.Core.Providers;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.App.Services;

public sealed class AutoUpdater
{
    private readonly AddonManager _manager;
    private readonly TrayService _tray;
    private readonly DispatcherTimer _timer = new();

    public AutoUpdater(AddonManager manager, TrayService tray)
    {
        _manager = manager;
        _tray = tray;
        _timer.Tick += async (_, _) => await RunAsync("scheduled check");
        Reschedule();
    }

    public void Reschedule()
    {
        _timer.Stop();
        var minutes = _manager.Settings.CheckIntervalMinutes;
        if (minutes <= 0) return;
        _timer.Interval = TimeSpan.FromMinutes(minutes);
        _timer.Start();
    }

    public static async Task<bool> WaitForNetworkAsync()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    using var req = new HttpRequestMessage(HttpMethod.Head, "https://api.github.com/");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    using var resp = await Http.Client.SendAsync(req, cts.Token);
                    return true;
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 + attempt * 2, 15)));
        }
        return false;
    }

    public async Task<UpdateSummary?> RunAsync(string reason, bool waitForNetwork = false)
    {
        var flavor = _manager.Flavor;
        if (flavor == null || _manager.IsBusy) return null;

        if (waitForNetwork && !await WaitForNetworkAsync())
        {
            Log.Warn("No internet connection - update check skipped");
            return null;
        }

        var install = _manager.Settings.AutoUpdate;
        Log.Info($"Auto-update run ({reason}); install={install}");
        UpdateSummary summary;
        try { summary = await _manager.UpdateAllAsync(install); }
        catch (Exception ex)
        {
            Log.Error("Auto-update failed", ex);
            return null;
        }

        if (!_manager.Settings.ShowNotifications) return summary;
        if (summary.Updated.Count > 0)
        {
            var wowRunning = WowProcess.IsRunning(flavor.Path);
            _tray.Notify($"Updated {summary.Updated.Count} addon{(summary.Updated.Count == 1 ? "" : "s")}",
                string.Join(", ", summary.Updated) + (wowRunning ? "\nType /reload in game to load them." : ""));
        }
        else if (summary.Available.Count > 0 && !install)
        {
            _tray.Notify($"{summary.Available.Count} addon update{(summary.Available.Count == 1 ? "" : "s")} available",
                string.Join(", ", summary.Available) + ". Open the manager to install them.");
        }
        if (summary.Failed.Count > 0)
        {
            _tray.Notify("Some addon updates failed",
                string.Join("\n", summary.Failed.Select(f => $"{f.Name}: {f.Error}")), warning: true);
        }
        return summary;
    }
}
