using HupAddonManager.Core.Toc;

namespace HupAddonManager.Core.Install;

public sealed record ZipAddonFolder(string ZipPrefix, string TargetName);

public static class ZipLayout
{
    public static List<ZipAddonFolder> Analyze(IEnumerable<string> entryPaths)
    {
        var paths = entryPaths.Select(p => p.Replace('\\', '/')).ToList();
        foreach (var p in paths)
        {
            if (!IsSafe(p))
                throw new InvalidDataException($"The zip contains an unsafe path ({p}). It was not installed.");
        }

        var tocDirs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!p.EndsWith(".toc", StringComparison.OrdinalIgnoreCase) || IsJunk(p)) continue;
            var slash = p.LastIndexOf('/');
            var dir = slash < 0 ? "" : p[..slash];
            var baseName = Path.GetFileNameWithoutExtension(p);
            if (!tocDirs.TryGetValue(dir, out var list)) tocDirs[dir] = list = new();
            list.Add(baseName);
        }
        if (tocDirs.Count == 0) return new();

        var roots = tocDirs.Keys
            .Where(d => !tocDirs.Keys.Any(other => other != d && IsAncestor(other, d)))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<ZipAddonFolder>();
        foreach (var dir in roots)
        {
            var lastSegment = dir.Length == 0 ? "" : dir[(dir.LastIndexOf('/') + 1)..];
            var bases = tocDirs[dir].Select(TocParser.StripFlavorSuffix).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string target = bases.FirstOrDefault(b => b.Equals(lastSegment, StringComparison.OrdinalIgnoreCase))
                            ?? bases.OrderBy(b => b.Length).First();

            if (result.Any(r => r.TargetName.Equals(target, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new ZipAddonFolder(dir, target));
        }
        return result;
    }

    private static bool IsAncestor(string ancestor, string dir) =>
        ancestor.Length == 0 ? dir.Length > 0 : dir.StartsWith(ancestor + "/", StringComparison.OrdinalIgnoreCase);

    public static bool IsJunk(string entryPath) =>
        entryPath.Replace('\\', '/').Split('/').Any(s => s == "__MACOSX" || s.StartsWith("._"));

    public static bool IsSafe(string entryPath)
    {
        var p = entryPath.Replace('\\', '/');
        if (p.StartsWith('/') || p.Contains(':')) return false;
        return !p.Split('/').Any(s => s == "..");
    }
}
