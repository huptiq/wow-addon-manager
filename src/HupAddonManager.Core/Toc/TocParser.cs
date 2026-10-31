using System.Text;
using System.Text.RegularExpressions;

namespace HupAddonManager.Core.Toc;

public sealed class TocInfo
{
    public string FolderName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string TocFile { get; init; } = "";
    public bool IsFlavorToc { get; init; }
    public string? Title { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Notes { get; init; }
    public List<int> Interfaces { get; init; } = new();
    public List<string> Dependencies { get; init; } = new();
    public bool LoadOnDemand { get; init; }
    public Dictionary<string, string> Fields { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? FolderName : Title!;

    public string? Field(string key) => Fields.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}

public static partial class TocParser
{
    public static readonly string[] KnownFlavorSuffixes =
    {
        "Mainline", "Classic", "Vanilla", "TBC", "BCC", "Wrath", "WOTLKC", "Cata", "Mists", "Camelot", "Forever", "Standard", "Era",
    };

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}|\|r|\|T[^|]*\|t|\|A[^|]*\|a")]
    private static partial Regex ColorCodes();

    [GeneratedRegex(@"^(?<base>.+?)[-_](?<flavor>Mainline|Classic|Vanilla|TBC|BCC|Wrath|WOTLKC|Cata|Mists|Camelot|Forever|Standard|Era)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlavorSuffix();

    public static string StripFlavorSuffix(string tocBaseName)
    {
        var m = FlavorSuffix().Match(tocBaseName);
        return m.Success ? m.Groups["base"].Value : tocBaseName;
    }

    public static string? GetFlavorSuffix(string tocBaseName)
    {
        var m = FlavorSuffix().Match(tocBaseName);
        return m.Success ? m.Groups["flavor"].Value : null;
    }

    public static string CleanTitle(string title) => ColorCodes().Replace(title, "").Trim();

    public static string? SelectToc(string folderPath, IReadOnlyList<string> flavorSuffixes, out bool isFlavorToc)
    {
        isFlavorToc = false;
        string[] tocs;
        try { tocs = Directory.GetFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly); }
        catch { return null; }
        if (tocs.Length == 0) return null;

        var folderName = Path.GetFileName(folderPath.TrimEnd('\\', '/'));
        var byName = tocs.ToDictionary(t => Path.GetFileNameWithoutExtension(t), StringComparer.OrdinalIgnoreCase);

        foreach (var suffix in flavorSuffixes)
        {
            foreach (var sep in new[] { "_", "-" })
            {
                if (byName.TryGetValue(folderName + sep + suffix, out var flavorToc))
                {
                    isFlavorToc = true;
                    return flavorToc;
                }
            }
        }
        if (byName.TryGetValue(folderName, out var plain)) return plain;

        var match = tocs.FirstOrDefault(t => StripFlavorSuffix(Path.GetFileNameWithoutExtension(t)).Equals(folderName, StringComparison.OrdinalIgnoreCase));
        return match ?? tocs[0];
    }

    public static TocInfo? ReadFolder(string folderPath, IReadOnlyList<string> flavorSuffixes)
    {
        var toc = SelectToc(folderPath, flavorSuffixes, out var isFlavor);
        if (toc == null) return null;
        string text;
        try { text = File.ReadAllText(toc, Encoding.UTF8); }
        catch { return null; }
        return Parse(text, Path.GetFileName(folderPath.TrimEnd('\\', '/')), folderPath, toc, isFlavor);
    }

    public static TocInfo Parse(string text, string folderName, string folderPath = "", string tocFile = "", bool isFlavorToc = false)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (!line.StartsWith("##")) continue;
            var body = line[2..];
            var colon = body.IndexOf(':');
            if (colon <= 0) continue;
            var key = body[..colon].Trim();
            var value = body[(colon + 1)..].Trim();
            if (key.Length == 0) continue;
            fields.TryAdd(key, value);
        }

        var interfaces = new List<int>();
        if (fields.TryGetValue("Interface", out var iface))
        {
            foreach (var part in iface.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out var n) && n > 0) interfaces.Add(n);
        }

        var deps = new List<string>();
        foreach (var key in new[] { "Dependencies", "RequiredDeps", "Dep", "Deps" })
            if (fields.TryGetValue(key, out var d))
                deps.AddRange(d.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        string? version = fields.TryGetValue("Version", out var v) ? v : null;
        if (version != null && (version.Length == 0 || version.Contains("@project-version@", StringComparison.OrdinalIgnoreCase)))
            version = null;

        return new TocInfo
        {
            FolderName = folderName,
            FolderPath = folderPath,
            TocFile = tocFile,
            IsFlavorToc = isFlavorToc,
            Title = fields.TryGetValue("Title", out var t) ? CleanTitle(t) : null,
            Version = version,
            Author = fields.TryGetValue("Author", out var a) ? CleanTitle(a) : null,
            Notes = fields.TryGetValue("Notes", out var n2) ? CleanTitle(n2) : null,
            Interfaces = interfaces,
            Dependencies = deps.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LoadOnDemand = fields.TryGetValue("LoadOnDemand", out var lod) && lod.Trim() == "1",
            Fields = fields,
        };
    }
}
