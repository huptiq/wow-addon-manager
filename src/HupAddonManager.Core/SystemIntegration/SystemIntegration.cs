using System.Diagnostics;
using HupAddonManager.Core.Providers;
using HupAddonManager.Core.Storage;
using Microsoft.Win32;

namespace HupAddonManager.Core.SystemIntegration;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HupAddonManager";
    public const string BackgroundArg = "--background";

    private static string Command => $"\"{Environment.ProcessPath}\" {BackgroundArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            if (enabled && current != Command) Set(true);
            else if (!enabled && current != null) Set(false);
        }
        catch (Exception ex) { Log.Warn("Could not update the autostart entry: " + ex.Message); }
    }
}

public sealed class AppUpdateInfo
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string DownloadUrl { get; init; }
    public string? Notes { get; init; }
}

public static class SelfUpdater
{
    public static async Task<AppUpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var doc = await Http.GetJsonAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest", ct).ConfigureAwait(false);
        var r = doc.RootElement;
        var tag = r.Str("tag_name") ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        var asset = r.Arr("assets").FirstOrDefault(a => (a.Str("name") ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var url = asset.ValueKind == System.Text.Json.JsonValueKind.Object ? asset.Str("browser_download_url") : null;
        if (url == null) return null;
        var current = AppInfo.Version;
        var cmp = new Version(v.Major, v.Minor, Math.Max(v.Build, 0)).CompareTo(new Version(current.Major, current.Minor, Math.Max(current.Build, 0)));
        return cmp > 0 ? new AppUpdateInfo { Version = v, Tag = tag, DownloadUrl = url, Notes = r.Str("body") } : null;
    }

    public static async Task<bool> ApplyAsync(AppUpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var dir = Path.GetDirectoryName(exe)!;
        var newExe = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".new.exe");
        var oldExe = exe + ".old";

        using (var resp = await Http.Client.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            await Http.EnsureOk(resp, info.DownloadUrl, ct).ConfigureAwait(false);
            var total = resp.Content.Headers.ContentLength;
            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(newExe);
            var buf = new byte[81920];
            long done = 0; int n;
            while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }

        if (File.Exists(oldExe)) File.Delete(oldExe);
        File.Move(exe, oldExe);
        File.Move(newExe, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Log.Info($"Updated app to {info.Tag}; restarting");
        return true;
    }

    public static void CleanupOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { }
    }
}

public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
    }
}
