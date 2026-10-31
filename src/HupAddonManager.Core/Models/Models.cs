using System.Text.Json.Serialization;

namespace HupAddonManager.Core.Models;

public sealed class ReleaseInfo
{
    public string Version { get; set; } = "";
    public string ReleaseId { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string? FileName { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string? Changelog { get; set; }
    public bool IsPrerelease { get; set; }
    public long? Size { get; set; }
    public bool FlavorMatched { get; set; }
    public Dictionary<string, string>? DownloadHeaders { get; set; }
}

public sealed class RemoteAddon
{
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    public string? Author { get; set; }
    public string? Summary { get; set; }
    public ReleaseInfo Latest { get; set; } = new();
}

public sealed class TrackedAddon
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public List<string> Folders { get; set; } = new();
    public string? InstalledVersion { get; set; }
    public string? InstalledReleaseId { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public bool Pinned { get; set; }
    public bool AllowPrerelease { get; set; }
    public bool AutoDetected { get; set; }

    public ReleaseInfo? Latest { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
}

public sealed class FlavorData
{
    public List<TrackedAddon> Addons { get; set; } = new();
    public List<string> NoAutoLink { get; set; } = new();
}

public sealed class AddonDatabase
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, FlavorData> Flavors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public FlavorData For(string flavorKey)
    {
        if (!Flavors.TryGetValue(flavorKey, out var data))
        {
            data = new FlavorData();
            Flavors[flavorKey] = data;
        }
        return data;
    }
}

public enum UpdateStatus
{
    NotChecked,
    UpToDate,
    UpdateAvailable,
    Unknown,
    Error,
    Unlinked,
    Missing,
    Updating,
}

public sealed class AppSettings
{
    public string? WowRoot { get; set; }
    public string SelectedFlavor { get; set; } = "_forever_";

    public bool AutoUpdate { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 60;
    public bool StartWithWindows { get; set; }
    public bool ExitAfterBootUpdate { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool WaitForWowToClose { get; set; } = true;
    public bool BackupWtfBeforeUpdate { get; set; } = true;
    public int WtfBackupsToKeep { get; set; } = 5;
    public int AddonBackupsToKeep { get; set; } = 3;
    public bool ShowNotifications { get; set; } = true;

    public string? CurseForgeApiKeyProtected { get; set; }
    public string? WagoApiKeyProtected { get; set; }
    public string? GitHubTokenProtected { get; set; }

    public bool CheckForAppUpdates { get; set; } = true;

    [JsonIgnore] public string? CurseForgeApiKey { get => Storage.Secrets.Unprotect(CurseForgeApiKeyProtected); set => CurseForgeApiKeyProtected = Storage.Secrets.Protect(value); }
    [JsonIgnore] public string? WagoApiKey { get => Storage.Secrets.Unprotect(WagoApiKeyProtected); set => WagoApiKeyProtected = Storage.Secrets.Protect(value); }
    [JsonIgnore] public string? GitHubToken { get => Storage.Secrets.Unprotect(GitHubTokenProtected); set => GitHubTokenProtected = Storage.Secrets.Protect(value); }
}
