using System.Net.Http.Headers;
using System.Text.Json;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Providers;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core.Search;

public sealed class SearchResult
{
    public string Provider { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Author { get; init; }
    public string? Summary { get; init; }
    public List<string> Categories { get; init; } = new();
    public long Downloads { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public string Link { get; init; } = "";
    public bool? ForFlavor { get; init; }
    public string? RepoKey { get; init; }
    public double Score { get; set; }
}

public sealed class SearchQuery
{
    public string Text { get; init; } = "";
    public bool OnlyForFlavor { get; init; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);
}

public sealed class SourceStatus
{
    public string Provider { get; init; } = "";
    public string SourceName { get; init; } = "";
    public int Count { get; init; }
    public string? Note { get; init; }

    public bool KeyRejected { get; init; }
}

public sealed class SearchOutcome
{
    public List<SearchResult> Results { get; } = new();
    public List<SourceStatus> Sources { get; } = new();
}

public interface ISearchSource
{
    string Provider { get; }
    string DisplayName { get; }
    string? Unavailable(AppSettings settings);
    Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct);
}

public sealed class AddonSearch
{
    private readonly ISearchSource[] _sources;

    public AddonSearch() : this(
        new WowUpHubSearch(),
        new CurseForgeSearch(),
        new WoWInterfaceSearch(),
        new GitHubSearch(),
        new WagoSearch(),
        new TukuiSearch())
    {
    }

    internal AddonSearch(params ISearchSource[] sources) => _sources = sources;

    public IReadOnlyList<ISearchSource> Sources => _sources;

    public async Task<SearchOutcome> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        var outcome = new SearchOutcome();

        var tasks = _sources.Select(async s =>
        {
            var unavailable = s.Unavailable(ctx.Settings);
            if (unavailable != null) return (s, list: new List<SearchResult>(), note: unavailable, rejected: false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var list = await s.SearchAsync(query, ctx, timeout.Token).ConfigureAwait(false);
                return (s, list, note: (string?)null, rejected: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (s, list: new List<SearchResult>(), note: "timed out", rejected: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Search on {s.DisplayName} failed: {ex.Message}");
                if (ex is ProviderException { IsAuthFailure: true })
                    return (s, list: new List<SearchResult>(), note: s.Provider == "wago" ? "key rejected - needs a Patreon key" : "API key rejected", rejected: true);
                return (s, list: new List<SearchResult>(), note: ex is ProviderException ? ex.Message : "failed", rejected: false);
            }
        }).ToList();

        var done = await Task.WhenAll(tasks).ConfigureAwait(false);

        var repoSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, list, note, rejected) in done)
        {
            var kept = 0;
            foreach (var r in list)
            {
                if (query.OnlyForFlavor && r.ForFlavor != true) continue;
                if (r.RepoKey != null && !repoSeen.Add(r.RepoKey)) continue;
                r.Score = Score(r, query.Text);
                outcome.Results.Add(r);
                kept++;
            }
            outcome.Sources.Add(new SourceStatus { Provider = s.Provider, SourceName = s.DisplayName, Count = kept, Note = note, KeyRejected = rejected });
        }

        outcome.Results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return outcome;
    }

    public static double Score(SearchResult r, string text)
    {
        var score = Math.Log10(Math.Max(1, r.Downloads)) * 2;
        if (r.ForFlavor == true) score += 25;
        var q = text.Trim();
        if (q.Length > 0)
        {
            if (r.Name.Equals(q, StringComparison.OrdinalIgnoreCase)) score += 60;
            else if (r.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += 35;
            else if (r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 20;
            else if (r.Categories.Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 8;
            else if (Matches(q, r.Summary)) score += 8;
        }
        if (r.Updated is { } u && u > DateTimeOffset.Now.AddYears(-1)) score += 5;
        return score;
    }

    internal static bool Matches(string text, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => fields.Any(f => f != null && f.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool MentionsFlavor(WowFlavor flavor, params string?[] fields) =>
        flavor.ReleaseFlavors.Any(rf => fields.Any(f => f != null && f.Contains(rf, StringComparison.OrdinalIgnoreCase)));
}

public sealed class WowUpHubSearch : ISearchSource
{
    public string Provider => "wowup";
    public string DisplayName => "WowUp Hub";
    public string? Unavailable(AppSettings settings) => null;

    private static string GameType(WowFlavor f) => f.ReleaseFlavors.FirstOrDefault() switch
    {
        "forever" => "forever",
        "mainline" => "retail",
        "classic" => "classic",
        "bcc" => "burningCrusade",
        "wrath" => "wotlk",
        "cata" => "cata",
        "mists" => "mists",
        _ => "retail",
    };

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        var gt = GameType(ctx.Flavor);
        var term = query.HasText ? query.Text.Trim() : null;
        var url = term == null
            ? $"https://hub.wowup.io/addons/featured/{gt}?count=50"
            : $"https://hub.wowup.io/addons/search/{gt}?query={Uri.EscapeDataString(term)}";
        using var doc = await Http.GetJsonAsync(url, ct).ConfigureAwait(false);
        var list = new List<SearchResult>();
        foreach (var a in doc.RootElement.Arr("addons"))
        {
            var id = a.Str("id");
            if (id == null) continue;
            var description = Text.HtmlToText(a.Str("description"));
            var topics = a.Arr("topics").Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList();
            var name = a.Str("repository_name") ?? id;
            var repo = a.Str("repository");
            var release = a.Arr("releases").FirstOrDefault();
            list.Add(new SearchResult
            {
                Provider = Provider,
                SourceName = DisplayName,
                ProjectId = id,
                Name = name,
                Author = a.Str("owner_name"),
                Summary = FirstLine(description),
                Categories = topics,
                Downloads = a.Long("total_download_count") ?? 0,
                Updated = release.ValueKind == JsonValueKind.Object ? release.Date("published_at") : null,
                Link = $"https://wowup.io/addons/{id}",
                ForFlavor = true,
                RepoKey = GitHubSearch.RepoKeyFromUrl(repo),
            });
        }
        return list;
    }

    internal static string? FirstLine(string? text) =>
        text?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Length > 25 && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase));
}

public sealed class WoWInterfaceSearch : ISearchSource
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<JsonElement>? _files;
    private static Dictionary<string, string>? _categories;
    private static DateTime _loadedAt;

    public string Provider => "wowinterface";
    public string DisplayName => "WoWInterface";
    public string? Unavailable(AppSettings settings) => null;

    private static string CacheDir => Directory.CreateDirectory(Path.Combine(AppPaths.DataDir, "cache")).FullName;

    private static async Task<JsonDocument> CachedJsonAsync(string url, string file, CancellationToken ct)
    {
        var path = Path.Combine(CacheDir, file);
        if (!File.Exists(path) || File.GetLastWriteTime(path) < DateTime.Now.AddHours(-12))
        {
            try
            {
                var text = await Http.GetStringAsync(url, ct).ConfigureAwait(false);
                await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
            }
            catch (Exception) when (File.Exists(path) && !ct.IsCancellationRequested) { }
        }
        await using var fs = File.OpenRead(path);
        return await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task LoadAsync(CancellationToken ct)
    {
        if (_files != null && _loadedAt > DateTime.Now.AddHours(-12)) return;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_files != null && _loadedAt > DateTime.Now.AddHours(-12)) return;
            using var cats = await CachedJsonAsync("https://api.mmoui.com/v3/game/WOW/categorylist.json", "wowi_categories.json", ct).ConfigureAwait(false);
            _categories = cats.RootElement.Arr()
                .Where(c => c.Str("UICATID") != null)
                .GroupBy(c => c.Str("UICATID")!)
                .ToDictionary(g => g.Key, g => g.First().Str("UICATTitle") ?? "");
            var files = await CachedJsonAsync("https://api.mmoui.com/v3/game/WOW/filelist.json", "wowi_files.json", ct).ConfigureAwait(false);
            _files = files.RootElement.Arr().ToList();
            _loadedAt = DateTime.Now;
        }
        finally { Gate.Release(); }
    }

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        await LoadAsync(ct).ConfigureAwait(false);
        var list = new List<SearchResult>();
        foreach (var f in _files!)
        {
            var name = f.Str("UIName") ?? "";
            var author = f.Str("UIAuthorName");
            var cat = _categories!.TryGetValue(f.Str("UICATID") ?? "", out var c) ? c : "";
            var dirs = string.Join(' ', f.Arr("UIDir").Select(d => d.GetString()));
            if (!AddonSearch.Matches(query.Text, name, author, cat, dirs)) continue;
            var id = f.Str("UID") ?? "";
            var date = f.Long("UIDate");
            list.Add(new SearchResult
            {
                Provider = Provider,
                SourceName = DisplayName,
                ProjectId = id,
                Name = name,
                Author = author,
                Summary = string.Join(", ", f.Arr("UICompatibility").Select(x => x.Str("name")).Where(n => n != null).Distinct().Take(3)),
                Categories = cat.Length > 0 ? new List<string> { cat } : new(),
                Downloads = f.Long("UIDownloadTotal") ?? 0,
                Updated = date is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                Link = f.Str("UIFileInfoURL") ?? $"https://www.wowinterface.com/downloads/info{id}",
                ForFlavor = AddonSearch.MentionsFlavor(ctx.Flavor, name, dirs) ? true : null,
            });
        }
        return list.OrderByDescending(r => AddonSearch.Score(r, query.Text)).Take(60).ToList();
    }
}

public sealed class CurseForgeSearch : ISearchSource
{
    private const string Api = "https://api.curseforge.com/v1";
    public string Provider => "curseforge";
    public string DisplayName => "CurseForge";
    public string? Unavailable(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.CurseForgeApiKey) ? "needs a free API key (Settings)" : null;

    private static Action<HttpRequestMessage> Auth(ProviderContext ctx) =>
        r => r.Headers.TryAddWithoutValidation("x-api-key", ctx.Settings.CurseForgeApiKey);

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        var url = $"{Api}/mods/search?gameId=1&sortField=2&sortOrder=desc&pageSize=50";
        if (query.HasText) url += "&searchFilter=" + Uri.EscapeDataString(query.Text.Trim());
        return await SearchUrlAsync(url, ctx, ct).ConfigureAwait(false);
    }

    private static async Task<List<SearchResult>> SearchUrlAsync(string url, ProviderContext ctx, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync(url, ct, Auth(ctx)).ConfigureAwait(false);
        var prefix = ctx.Flavor.GameVersionPrefix;
        return doc.RootElement.Arr("data").Select(m =>
        {
            var versions = m.Arr("latestFilesIndexes").Select(i => i.Str("gameVersion") ?? "").ToList();
            var slug = m.Str("slug");
            return new SearchResult
            {
                Provider = "curseforge",
                SourceName = "CurseForge",
                ProjectId = m.Str("id") ?? "",
                Name = m.Str("name") ?? slug ?? "",
                Author = m.Arr("authors").Select(a => a.Str("name")).FirstOrDefault(),
                Summary = m.Str("summary"),
                Categories = m.Arr("categories").Select(c => c.Str("name") ?? "").Where(c => c.Length > 0).ToList(),
                Downloads = m.Long("downloadCount") ?? 0,
                Updated = m.Date("dateReleased") ?? m.Date("dateModified"),
                Link = m.Prop("links").Str("websiteUrl") ?? $"https://www.curseforge.com/wow/addons/{slug}",
                ForFlavor = prefix != null && versions.Any(v => v == prefix || v.StartsWith(prefix + ".")) ? true
                    : AddonSearch.MentionsFlavor(ctx.Flavor, m.Str("name"), slug) ? true : null,
            };
        }).ToList();
    }
}

public sealed class GitHubSearch : ISearchSource
{
    public string Provider => "github";
    public string DisplayName => "GitHub";
    public string? Unavailable(AppSettings settings) => null;

    internal static string? RepoKeyFromUrl(string? url)
    {
        if (url == null || !url.Contains("github.com/", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = url[(url.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase) + 11)..].Trim('/').Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}".ToLowerInvariant() : null;
    }

    internal static string BuildQuery(SearchQuery query) =>
        query.Text.Trim() + " in:name,description,topics topic:wow-addon fork:false";

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        if (!query.HasText) return new();
        var q = Uri.EscapeDataString(BuildQuery(query));
        using var doc = await Http.GetJsonAsync($"https://api.github.com/search/repositories?q={q}&sort=stars&per_page=30", ct, r =>
        {
            r.Headers.Accept.Clear();
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            if (!string.IsNullOrWhiteSpace(ctx.Settings.GitHubToken)) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ctx.Settings.GitHubToken);
        }).ConfigureAwait(false);
        return doc.RootElement.Arr("items").Select(r =>
        {
            var full = r.Str("full_name") ?? "";
            var topics = r.Arr("topics").Select(t => t.GetString() ?? "").ToList();
            return new SearchResult
            {
                Provider = Provider,
                SourceName = DisplayName,
                ProjectId = full,
                Name = r.Str("name") ?? full,
                Author = r.Prop("owner").Str("login"),
                Summary = r.Str("description"),
                Categories = topics.Where(t => t is not ("wow-addon" or "world-of-warcraft" or "wow" or "addon")).ToList(),
                Downloads = (r.Long("stargazers_count") ?? 0) * 50,
                Updated = r.Date("pushed_at"),
                Link = r.Str("html_url") ?? $"https://github.com/{full}",
                ForFlavor = AddonSearch.MentionsFlavor(ctx.Flavor, full, r.Str("description"), string.Join(' ', topics)) ? true : null,
                RepoKey = full.ToLowerInvariant(),
            };
        }).ToList();
    }
}

public sealed class WagoSearch : ISearchSource
{
    public string Provider => "wago";
    public string DisplayName => "Wago Addons";

    public string? Unavailable(AppSettings settings) => null;

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        var listings = await WagoWeb.SearchAsync(query.Text, ctx.Flavor.WagoGameVersion, ct).ConfigureAwait(false);

        return listings.Select(l => new SearchResult
            {
                Provider = Provider,
                SourceName = DisplayName,
                ProjectId = l.Slug,
                Name = l.Name,
                Author = l.Author,
                Summary = l.Summary,
                Downloads = l.Downloads,
                Updated = l.Updated,
                Link = l.Link,
                ForFlavor = true,
            }).ToList();
    }
}

public sealed class TukuiSearch : ISearchSource
{
    public string Provider => "tukui";
    public string DisplayName => "Tukui";
    public string? Unavailable(AppSettings settings) => null;

    public async Task<List<SearchResult>> SearchAsync(SearchQuery query, ProviderContext ctx, CancellationToken ct)
    {
        if (!query.HasText) return new();
        using var doc = await Http.GetJsonAsync("https://api.tukui.org/v1/addons", ct).ConfigureAwait(false);
        var prefix = ctx.Flavor.GameVersionPrefix;
        return doc.RootElement.Arr()
            .Where(a => AddonSearch.Matches(query.Text, a.Str("name"), a.Str("small_desc"), a.Str("author"), "UI replacement"))
            .Select(a => new SearchResult
            {
                Provider = Provider,
                SourceName = DisplayName,
                ProjectId = a.Str("slug") ?? "",
                Name = a.Str("name") ?? "",
                Author = a.Str("author"),
                Summary = a.Str("small_desc"),
                Categories = new List<string> { "UI replacement" },
                Downloads = a.Long("downloads") ?? 0,
                Updated = a.Date("last_update"),
                Link = a.Str("web_url") ?? $"https://tukui.org/{a.Str("slug")}",
                ForFlavor = prefix != null && a.Arr("patch").Any(p => (p.GetString() ?? "").StartsWith(prefix)) ? true : null,
            }).ToList();
    }
}
