using System.IO.Compression;
using System.Text.Json;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core.Install;

public sealed class AddonBackupInfo
{
    public string Path { get; init; } = "";
    public string AddonName { get; init; } = "";
    public string? Version { get; init; }
    public string? ReleaseId { get; init; }
    public string? TrackedId { get; init; }
    public DateTime Date { get; init; }
    public List<string> Folders { get; init; } = new();
    public long Size { get; init; }
}

public sealed class WtfBackupInfo
{
    public string Path { get; init; } = "";
    public DateTime Date { get; init; }
    public long Size { get; init; }
}

public static class BackupService
{
    private const string ManifestName = "_hup_backup.json";
    private const string WtfDirName = "_WTF";

    private sealed class Manifest
    {
        public string AddonName { get; set; } = "";
        public string? Version { get; set; }
        public string? ReleaseId { get; set; }
        public string? TrackedId { get; set; }
        public List<string> Folders { get; set; } = new();
        public DateTime Date { get; set; }
    }

    public static string FlavorDir(string flavorKey) =>
        Directory.CreateDirectory(Path.Combine(AppPaths.BackupsDir, flavorKey.Trim('_'))).FullName;

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (s.Length > 60) s = s[..60];
        return s.Length == 0 ? "addon" : s;
    }

    public static string? BackupAddon(string flavorKey, string addOnsDir, string addonName, string? version, string? releaseId,
        string? trackedId, IEnumerable<string> folders, int keep)
    {
        var existing = folders.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => Directory.Exists(Path.Combine(addOnsDir, f))).ToList();
        if (existing.Count == 0) return null;

        var dir = Directory.CreateDirectory(Path.Combine(FlavorDir(flavorKey), Sanitize(addonName))).FullName;
        var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Sanitize(version ?? "unknown")}.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
        {
            foreach (var folder in existing)
                AddDirectory(zip, Path.Combine(addOnsDir, folder), folder);
            var manifest = new Manifest { AddonName = addonName, Version = version, ReleaseId = releaseId, TrackedId = trackedId, Folders = existing, Date = DateTime.Now };
            using var s = zip.CreateEntry(ManifestName).Open();
            JsonSerializer.Serialize(s, manifest, JsonStore.Options);
        }
        Prune(dir, keep);
        Log.Info($"Backed up {addonName} {version} -> {Path.GetFileName(file)}");
        return file;
    }

    public static List<AddonBackupInfo> ListAddonBackups(string flavorKey)
    {
        var list = new List<AddonBackupInfo>();
        foreach (var dir in Directory.GetDirectories(FlavorDir(flavorKey)))
        {
            if (Path.GetFileName(dir) == WtfDirName) continue;
            foreach (var file in Directory.GetFiles(dir, "*.zip"))
            {
                try
                {
                    using var zip = ZipFile.OpenRead(file);
                    var entry = zip.GetEntry(ManifestName);
                    Manifest? m = null;
                    if (entry != null) { using var s = entry.Open(); m = JsonSerializer.Deserialize<Manifest>(s, JsonStore.Options); }
                    list.Add(new AddonBackupInfo
                    {
                        Path = file,
                        AddonName = m?.AddonName ?? Path.GetFileName(dir),
                        Version = m?.Version,
                        ReleaseId = m?.ReleaseId,
                        TrackedId = m?.TrackedId,
                        Folders = m?.Folders ?? new(),
                        Date = m?.Date ?? File.GetCreationTime(file),
                        Size = new FileInfo(file).Length,
                    });
                }
                catch (Exception ex) { Log.Warn($"Unreadable backup {file}: {ex.Message}"); }
            }
        }
        return list.OrderByDescending(b => b.Date).ToList();
    }

    public static string? BackupWtf(WowFlavor flavor, int keep, string reason)
    {
        if (!Directory.Exists(flavor.WtfPath)) return null;
        var dir = Directory.CreateDirectory(Path.Combine(FlavorDir(flavor.Key), WtfDirName)).FullName;
        var file = Path.Combine(dir, $"WTF_{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            AddDirectory(zip, flavor.WtfPath, "WTF", skip: f => f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".old", StringComparison.OrdinalIgnoreCase));
        Prune(dir, keep);
        Log.Info($"WTF backup created ({reason}): {Path.GetFileName(file)}");
        return file;
    }

    public static List<WtfBackupInfo> ListWtfBackups(string flavorKey)
    {
        var dir = Path.Combine(FlavorDir(flavorKey), WtfDirName);
        if (!Directory.Exists(dir)) return new();
        return Directory.GetFiles(dir, "*.zip")
            .Select(f => new WtfBackupInfo { Path = f, Date = File.GetLastWriteTime(f), Size = new FileInfo(f).Length })
            .OrderByDescending(b => b.Date).ToList();
    }

    public static void RestoreWtf(WowFlavor flavor, WtfBackupInfo backup, int keep)
    {
        BackupWtf(flavor, keep + 1, "before restore");
        using var zip = ZipFile.OpenRead(backup.Path);
        var root = Path.GetFullPath(flavor.WtfPath);
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith("WTF/", StringComparison.OrdinalIgnoreCase) || !ZipLayout.IsSafe(path)) continue;
            var rel = path[4..];
            if (rel.Length == 0 || rel.EndsWith('/')) continue;
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            if (!dest.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
        Log.Info($"WTF restored from {Path.GetFileName(backup.Path)}");
    }

    public static void Delete(string path)
    {
        if (File.Exists(path) && Path.GetFullPath(path).StartsWith(AppPaths.BackupsDir, StringComparison.OrdinalIgnoreCase))
            File.Delete(path);
    }

    private static void AddDirectory(ZipArchive zip, string dir, string entryPrefix, Func<string, bool>? skip = null)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (skip?.Invoke(file) == true) continue;
            var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
            try
            {
                zip.CreateEntryFromFile(file, $"{entryPrefix}/{rel}", CompressionLevel.Fastest);
            }
            catch (IOException ex) { Log.Warn($"Skipped locked file in backup: {rel} ({ex.Message})"); }
        }
    }

    private static void Prune(string dir, int keep)
    {
        if (keep < 1) keep = 1;
        foreach (var old in Directory.GetFiles(dir, "*.zip").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(keep))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
