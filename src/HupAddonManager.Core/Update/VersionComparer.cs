using System.Text.RegularExpressions;
using HupAddonManager.Core.Models;

namespace HupAddonManager.Core.Update;

public static partial class VersionComparer
{
    [GeneratedRegex(@"\d+(?:\.\d+)+|\d+")]
    private static partial Regex NumberGroup();

    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "";
        var v = version.Trim().ToLowerInvariant();
        if (v.EndsWith(".zip")) v = v[..^4];
        if (v.StartsWith("version")) v = v[7..].Trim();
        if (v.StartsWith("v") && v.Length > 1 && (char.IsDigit(v[1]) || v[1] == '.')) v = v[1..];
        if (v.StartsWith(".")) v = v[1..];
        return v;
    }

    public static int[]? NumericParts(string? version)
    {
        var n = Normalize(version);
        var m = NumberGroup().Match(n);
        if (!m.Success) return null;
        var parts = m.Value.Split('.');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i])) return null;
        return nums;
    }

    public static int? Compare(string? installed, string? latest)
    {
        var a = Normalize(installed);
        var b = Normalize(latest);
        if (a.Length == 0 || b.Length == 0) return null;
        if (a == b) return 0;

        var na = NumericParts(a);
        var nb = NumericParts(b);
        if (na == null || nb == null) return null;

        var len = Math.Max(na.Length, nb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < na.Length ? na[i] : 0;
            var y = i < nb.Length ? nb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        var preA = IsPrereleaseSuffix(a);
        var preB = IsPrereleaseSuffix(b);
        if (preA && !preB) return -1;
        if (!preA && preB) return 1;
        return 0;
    }

    private static bool IsPrereleaseSuffix(string normalized)
    {
        var m = NumberGroup().Match(normalized);
        if (!m.Success) return false;
        var suffix = normalized[(m.Index + m.Length)..];
        return suffix.StartsWith('-') || suffix.Contains("alpha") || suffix.Contains("beta") || suffix.Contains("rc");
    }

    public static UpdateStatus StatusFor(TrackedAddon addon, ReleaseInfo latest)
    {
        if (!string.IsNullOrEmpty(addon.InstalledReleaseId) && !string.IsNullOrEmpty(latest.ReleaseId))
            return addon.InstalledReleaseId == latest.ReleaseId ? UpdateStatus.UpToDate : UpdateStatus.UpdateAvailable;

        return Compare(addon.InstalledVersion, latest.Version) switch
        {
            < 0 => UpdateStatus.UpdateAvailable,
            >= 0 => UpdateStatus.UpToDate,
            null => UpdateStatus.Unknown,
        };
    }
}
