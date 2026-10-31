using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using HupAddonManager.Core.Storage;
using Microsoft.Win32;

namespace HupAddonManager.Core.Wow;

public sealed class WowFlavor
{
    public string Key { get; init; } = "";
    public string Path { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? ClientVersion { get; init; }
    public string[] TocSuffixes { get; init; } = Array.Empty<string>();
    public string[] ReleaseFlavors { get; init; } = Array.Empty<string>();
    public string WagoGameVersion { get; init; } = "retail";

    public string AddOnsPath => System.IO.Path.Combine(Path, "Interface", "AddOns");
    public string InterfacePath => System.IO.Path.Combine(Path, "Interface");
    public string WtfPath => System.IO.Path.Combine(Path, "WTF");

    public int? InterfaceVersion => WowFlavors.InterfaceFromVersion(ClientVersion);

    public string? GameVersionPrefix
    {
        get
        {
            var parts = ClientVersion?.Split('.');
            return parts is { Length: >= 2 } ? $"{parts[0]}.{parts[1]}" : null;
        }
    }

    public override string ToString() => ClientVersion == null ? DisplayName : $"{DisplayName} ({ClientVersion})";
}

public static class WowFlavors
{
    private static readonly Dictionary<string, string> ProductToFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wow"] = "_retail_", ["wowt"] = "_ptr_", ["wowxptr"] = "_xptr_", ["wow_beta"] = "_beta_",
        ["wow_classic"] = "_classic_", ["wow_classic_ptr"] = "_classic_ptr_", ["wow_classic_beta"] = "_classic_beta_",
        ["wow_classic_era"] = "_classic_era_", ["wow_classic_era_ptr"] = "_classic_era_ptr_",
        ["wowf"] = "_forever_",
    };

    public static int? InterfaceFromVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var parts = version.Split('.');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch)) return null;
        return major * 10000 + minor * 100 + patch;
    }

    public static string PrettyName(string key) => key.ToLowerInvariant() switch
    {
        "_forever_" => "WoW: Forever",
        "_retail_" => "Retail",
        "_ptr_" => "Retail PTR",
        "_xptr_" => "Retail XPTR",
        "_beta_" => "Retail Beta",
        "_classic_" => "Classic",
        "_classic_ptr_" => "Classic PTR",
        "_classic_beta_" => "Classic Beta",
        "_classic_era_" => "Classic Era",
        "_classic_era_ptr_" => "Classic Era PTR",
        "_anniversary_" => "Anniversary",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Trim('_').Replace('_', ' ')),
    };

    public static WowFlavor Create(string flavorPath, IReadOnlyDictionary<string, string>? buildInfoVersions = null)
    {
        var key = System.IO.Path.GetFileName(flavorPath.TrimEnd('\\', '/'));
        var version = ReadExeVersion(flavorPath);
        if (version == null && buildInfoVersions != null && buildInfoVersions.TryGetValue(key, out var v)) version = v;

        string[] suffixes, releaseFlavors;
        string wago;
        int major = 0;
        if (version != null) int.TryParse(version.Split('.')[0], out major);

        if (key.Equals("_forever_", StringComparison.OrdinalIgnoreCase))
        {
            suffixes = new[] { "Forever", "Camelot" };
            releaseFlavors = new[] { "forever", "camelot" };
            wago = "forever";
        }
        else if (key.StartsWith("_classic", StringComparison.OrdinalIgnoreCase) || key.StartsWith("_anniversary", StringComparison.OrdinalIgnoreCase))
        {
            (suffixes, releaseFlavors, wago) = major switch
            {
                1 => (new[] { "Vanilla", "Classic" }, new[] { "classic" }, "classic"),
                2 => (new[] { "TBC", "BCC", "Classic" }, new[] { "bcc" }, "bc"),
                3 => (new[] { "Wrath", "WOTLKC", "Classic" }, new[] { "wrath" }, "wotlk"),
                4 => (new[] { "Cata", "Classic" }, new[] { "cata" }, "cata"),
                5 => (new[] { "Mists", "Classic" }, new[] { "mists" }, "mop"),
                _ => (new[] { "Classic" }, new[] { "classic" }, "classic"),
            };
        }
        else
        {
            suffixes = new[] { "Mainline", "Standard" };
            releaseFlavors = new[] { "mainline", "retail" };
            wago = "retail";
        }

        return new WowFlavor
        {
            Key = key,
            Path = flavorPath,
            DisplayName = PrettyName(key),
            ClientVersion = version,
            TocSuffixes = suffixes,
            ReleaseFlavors = releaseFlavors,
            WagoGameVersion = wago,
        };
    }

    private static string? ReadExeVersion(string flavorPath)
    {
        try
        {
            foreach (var exe in Directory.GetFiles(flavorPath, "Wow*.exe"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(exe);
                if (name.Contains("Error", StringComparison.OrdinalIgnoreCase)) continue;
                var fv = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                if (!string.IsNullOrWhiteSpace(fv) && Regex.IsMatch(fv, @"^\d+\.\d+\.\d+")) return fv.Trim();
            }
        }
        catch { }
        return null;
    }

    public static Dictionary<string, string> ReadBuildInfo(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var file = System.IO.Path.Combine(root, ".build.info");
            if (!File.Exists(file)) return result;
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2) return result;
            var header = lines[0].Split('|').Select(h => h.Split('!')[0]).ToList();
            int prodIdx = header.IndexOf("Product"), verIdx = header.IndexOf("Version");
            if (prodIdx < 0 || verIdx < 0) return result;
            foreach (var line in lines.Skip(1))
            {
                var cols = line.Split('|');
                if (cols.Length <= Math.Max(prodIdx, verIdx)) continue;
                if (ProductToFolder.TryGetValue(cols[prodIdx], out var folder) && cols[verIdx].Length > 0)
                    result[folder] = cols[verIdx];
            }
        }
        catch { }
        return result;
    }
}

public static class WowLocator
{
    public static bool IsWowRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        return GetFlavorDirs(path).Any();
    }

    private static IEnumerable<string> GetFlavorDirs(string root)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.Length > 2 && name.StartsWith('_') && name.EndsWith('_') &&
                (Directory.Exists(Path.Combine(d, "Interface")) || Directory.GetFiles(d, "Wow*.exe").Length > 0))
                yield return d;
        }
    }

    public static List<WowFlavor> GetFlavors(string root)
    {
        var buildInfo = WowFlavors.ReadBuildInfo(root);
        return GetFlavorDirs(root)
            .Select(d => WowFlavors.Create(d, buildInfo))
            .OrderBy(f => f.Key.Equals("_forever_", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f.DisplayName)
            .ToList();
    }

    public static List<string> FindRoots()
    {
        var found = new List<string>();
        void Try(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                p = Path.GetFullPath(p.Trim().Trim('"'));
                var name = Path.GetFileName(p.TrimEnd('\\', '/'));
                if (name.StartsWith('_') && name.EndsWith('_')) p = Path.GetDirectoryName(p.TrimEnd('\\', '/'))!;
                if (IsWowRoot(p) && !found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(p);
            }
            catch { }
        }

        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var keyPath in new[] { @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", @"SOFTWARE\Blizzard Entertainment\World of Warcraft" })
            {
                try
                {
                    using var key = hive.OpenSubKey(keyPath);
                    Try(key?.GetValue("InstallPath") as string);
                    Try(key?.GetValue("GamePath") as string is { } gp ? Path.GetDirectoryName(gp) : null);
                }
                catch { }
            }
        }

        try
        {
            var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "product.db");
            if (File.Exists(db))
            {
                var text = Encoding.Latin1.GetString(File.ReadAllBytes(db));
                foreach (Match m in Regex.Matches(text, @"[A-Za-z]:[\\/][^\x00-\x1f""*?<>|]{0,200}?World of Warcraft"))
                    Try(m.Value.Replace('/', '\\'));
            }
        }
        catch { }

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var r = drive.RootDirectory.FullName;
                Try(Path.Combine(r, "World of Warcraft"));
                Try(Path.Combine(r, "Games", "World of Warcraft"));
                Try(Path.Combine(r, "Battle.net", "World of Warcraft"));
                Try(Path.Combine(r, "Blizzard", "World of Warcraft"));
                Try(Path.Combine(r, "Program Files (x86)", "World of Warcraft"));
                Try(Path.Combine(r, "Program Files", "World of Warcraft"));
                Try(Path.Combine(r, "Games", "Battle.net", "World of Warcraft"));
            }
            catch { }
        }

        return found.OrderBy(p => Directory.Exists(Path.Combine(p, "_forever_")) ? 0 : 1).ToList();
    }
}

public static class WowProcess
{
    public static bool IsRunning(string flavorPath)
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.ProcessName.Contains("Error", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var file = p.MainModule?.FileName;
                    if (file != null)
                    {
                        if (file.StartsWith(flavorPath, StringComparison.OrdinalIgnoreCase)) return true;
                        continue;
                    }
                }
                catch
                {
                    return true;
                }
            }
            finally { p.Dispose(); }
        }
        return false;
    }
}
