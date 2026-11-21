using System.Text.Json;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core.Providers;

public static class AssetPicker
{
    private static readonly string[] FlavorTokens =
        { "forever", "mainline", "retail", "classic", "vanilla", "era", "bcc", "tbc", "wrath", "wotlk", "wotlkc", "cata", "mists", "mop", "camelot" };

    private static readonly Dictionary<string, string[]> TokensForFlavor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forever"] = new[] { "forever", "camelot" },
        ["mainline"] = new[] { "mainline", "retail" },
        ["retail"] = new[] { "mainline", "retail" },
        ["classic"] = new[] { "classic", "vanilla", "era" },
        ["bcc"] = new[] { "bcc", "tbc" },
        ["wrath"] = new[] { "wrath", "wotlk", "wotlkc" },
        ["cata"] = new[] { "cata" },
        ["mists"] = new[] { "mists", "mop" },
    };

    private static IEnumerable<string> Tokens(string name) =>
        name.ToLowerInvariant().Split(new[] { '-', '_', '.', ' ', '+' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsVersionToken(string t) =>
        t.Length > 0 && (t.All(char.IsDigit) || (t.Length > 1 && (t[0] == 'v' || t[0] == 'r') && t[1..].All(char.IsDigit)));

    public static int Score(string fileName, WowFlavor flavor, out bool flavorMatched) =>
        Score(fileName, flavor, null, out flavorMatched, out _);

    /// <summary>
    /// Scores a release asset for the chosen flavor. When <paramref name="repoName"/> is given,
    /// an asset whose name (minus version/flavor words) is exactly the repo name is the main addon
    /// and scores highest; one that is the repo name plus an extra word (e.g. "Polestar_Recorder")
    /// is a companion artifact and is marked via <paramref name="looksCompanion"/>.
    /// </summary>
    public static int Score(string fileName, WowFlavor flavor, string? repoName, out bool flavorMatched, out bool looksCompanion)
    {
        flavorMatched = false;
        looksCompanion = false;
        var all = Tokens(Path.GetFileNameWithoutExtension(fileName)).ToList();
        var tokens = all.ToHashSet();
        var wanted = flavor.ReleaseFlavors.SelectMany(f => TokensForFlavor.TryGetValue(f, out var t) ? t : new[] { f }).ToHashSet();
        var score = 0;
        if (tokens.Overlaps(wanted)) { score += 100; flavorMatched = true; }
        else if (tokens.Overlaps(FlavorTokens)) score -= 50;
        if (tokens.Contains("nolib")) score -= 10;

        if (!string.IsNullOrEmpty(repoName))
        {
            var meaningful = all.Where(t => !IsVersionToken(t) && !FlavorTokens.Contains(t)).ToHashSet();
            var repoTokens = Tokens(repoName).Where(t => !IsVersionToken(t)).ToHashSet();
            if (meaningful.Count > 0 && repoTokens.Count > 0)
            {
                if (meaningful.SetEquals(repoTokens)) score += 40;
                else if (repoTokens.IsSubsetOf(meaningful)) { score += 5; looksCompanion = true; }
            }
        }
        return score;
    }

    public static T? Pick<T>(IEnumerable<T> items, Func<T, string> name, WowFlavor flavor, out bool flavorMatched)
    {
        T? best = default;
        var bestScore = int.MinValue;
        var matched = false;
        foreach (var item in items)
        {
            var n = name(item);
            if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var s = Score(n, flavor, out var m);
            if (s > bestScore) { best = item; bestScore = s; matched = m; }
        }
        flavorMatched = matched;
        return best;
    }
}

public sealed class GitHubProvider : IAddonProvider
{
    public string Key => "github";
    public string DisplayName => "GitHub";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        var host = url.Host.ToLowerInvariant();
        if (host != "github.com" && host != "www.github.com") return false;
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (seg.Length < 2) return false;
        var repo = seg[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? seg[1][..^4] : seg[1];
        projectId = $"{seg[0]}/{repo}";
        return true;
    }

    public string WebsiteFor(string projectId) => $"https://github.com/{projectId}";

    private static Action<HttpRequestMessage> Auth(ProviderContext ctx) => req =>
    {
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        var token = ctx.Settings.GitHubToken;
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    };

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        var api = $"https://api.github.com/repos/{projectId}";
        var parts = projectId.Split('/');
        var result = new RemoteAddon
        {
            Provider = Key,
            ProjectId = projectId,
            Name = parts.Last(),
            WebsiteUrl = WebsiteFor(projectId),
            Author = parts.First(),
        };

        using var relDoc = await Http.GetJsonAsync($"{api}/releases?per_page=15", ct, Auth(ctx)).ConfigureAwait(false);
        var releases = relDoc.RootElement.Arr()
            .Where(r => !r.Bool("draft") && (allowPrerelease || !r.Bool("prerelease")))
            .ToList();

        var repoName = parts.Last();
        foreach (var rel in releases)
        {
            var picked = await PickFromReleaseAsync(rel, repoName, ctx, ct).ConfigureAwait(false);
            if (picked != null)
            {
                result.Latest = picked;
                return result;
            }
        }

        using var repoDoc = await Http.GetJsonAsync(api, ct, Auth(ctx)).ConfigureAwait(false);
        var repo = repoDoc.RootElement;
        result.ProjectId = repo.Str("full_name") ?? projectId;
        result.Name = repo.Str("name") ?? result.Name;
        result.Summary = repo.Str("description");
        var branch = repo.Str("default_branch") ?? "main";
        using var commitDoc = await Http.GetJsonAsync($"{api}/commits/{Uri.EscapeDataString(branch)}", ct, Auth(ctx)).ConfigureAwait(false);
        var sha = commitDoc.RootElement.Str("sha") ?? throw new ProviderException("Could not read the latest commit.");
        var commit = commitDoc.RootElement.Prop("commit");
        result.Latest = new ReleaseInfo
        {
            Version = $"{branch}@{sha[..7]}",
            ReleaseId = sha,
            DownloadUrl = $"https://github.com/{result.ProjectId}/archive/{sha}.zip",
            FileName = $"{result.Name}-{sha[..7]}.zip",
            Date = commit.Prop("committer").Date("date"),
            Changelog = "No releases published - following the latest commit:\n\n" + commit.Str("message"),
        };
        return result;
    }

    private static async Task<ReleaseInfo?> PickFromReleaseAsync(JsonElement rel, string repoName, ProviderContext ctx, CancellationToken ct)
    {
        var assets = rel.Arr("assets").ToList();
        var tag = rel.Str("tag_name") ?? rel.Str("name") ?? "";
        JsonElement? chosen = null;
        var flavorMatched = false;
        var companion = false;

        var meta = assets.FirstOrDefault(a => string.Equals(a.Str("name"), "release.json", StringComparison.OrdinalIgnoreCase));
        if (meta.ValueKind == JsonValueKind.Object && meta.Str("browser_download_url") is { } metaUrl)
        {
            try
            {
                using var doc = await Http.GetJsonAsync(metaUrl, ct).ConfigureAwait(false);
                var entries = doc.RootElement.Arr("releases").ToList();
                var match = entries
                    .Where(e => e.Arr("metadata").Any(m => ctx.Flavor.ReleaseFlavors.Contains(m.Str("flavor") ?? "", StringComparer.OrdinalIgnoreCase)))
                    .OrderBy(e => e.Bool("nolib") ? 1 : 0)
                    .FirstOrDefault();
                if (match.ValueKind == JsonValueKind.Object)
                {
                    var file = match.Str("filename");
                    var asset = assets.FirstOrDefault(a => string.Equals(a.Str("name"), file, StringComparison.OrdinalIgnoreCase));
                    if (asset.ValueKind == JsonValueKind.Object) { chosen = asset; flavorMatched = true; }
                }
            }
            catch { }
        }

        if (chosen == null)
        {
            var best = int.MinValue;
            foreach (var a in assets)
            {
                var n = a.Str("name") ?? "";
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                var s = AssetPicker.Score(n, ctx.Flavor, repoName, out var m, out var comp);
                if (s > best) { best = s; chosen = a; flavorMatched = m; companion = comp; }
            }
        }

        var release = new ReleaseInfo
        {
            Version = tag,
            Date = rel.Date("published_at") ?? rel.Date("created_at"),
            Changelog = rel.Str("body"),
            IsPrerelease = rel.Bool("prerelease"),
        };

        if (chosen is { } a2)
        {
            // This release only ships a companion artifact (e.g. "Polestar_Recorder"), not the
            // addon itself. Skip it so an older release with the real addon zip is used instead.
            if (companion && !flavorMatched) return null;
            release.ReleaseId = a2.Str("id") ?? tag;
            release.DownloadUrl = a2.Str("browser_download_url") ?? "";
            release.FileName = a2.Str("name");
            release.Size = a2.Long("size");
            release.FlavorMatched = flavorMatched;
            return release;
        }

        // The release has binary assets but no addon zip (e.g. desktop app installers). It is not
        // a WoW addon release, so skip it. Only fall back to the source archive when a release
        // ships nothing at all, which is how some addons publish source-only tags.
        if (assets.Count > 0) return null;
        var zipball = rel.Str("zipball_url");
        if (string.IsNullOrEmpty(zipball)) return null;
        release.ReleaseId = "tag:" + tag;
        release.DownloadUrl = zipball;
        release.FileName = tag + ".zip";
        return release;
    }
}
