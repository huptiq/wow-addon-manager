using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Update;

namespace HupAddonManager.Core.Providers;

public sealed partial class WoWInterfaceProvider : IAddonProvider
{
    public string Key => "wowinterface";
    public string DisplayName => "WoWInterface";

    [GeneratedRegex(@"(?:info|download|id=|fileinfo\.php\?id=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex IdPattern();

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        if (!url.Host.EndsWith("wowinterface.com", StringComparison.OrdinalIgnoreCase)) return false;
        var m = IdPattern().Match(url.PathAndQuery);
        if (!m.Success) return false;
        projectId = m.Groups[1].Value;
        return true;
    }

    public string WebsiteFor(string projectId) => $"https://www.wowinterface.com/downloads/info{projectId}";

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync($"https://api.mmoui.com/v3/game/WOW/filedetails/{projectId}.json", ct).ConfigureAwait(false);
        var item = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.Arr().FirstOrDefault() : doc.RootElement;
        if (item.ValueKind != JsonValueKind.Object || item.Str("UIDownload") == null)
            throw new ProviderException("WoWInterface returned no file for this addon.");

        var fileName = item.Str("UIFileName") ?? "";
        AssetPicker.Score(fileName, ctx.Flavor, out var matched);
        var dateMs = item.Long("UIDate");
        var version = item.Str("UIVersion") ?? "";
        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = projectId,
            Name = item.Str("UIName") ?? projectId,
            WebsiteUrl = WebsiteFor(projectId),
            Author = item.Str("UIAuthorName"),
            Summary = Text.HtmlToText(item.Str("UIDescription"))?.Split('\n').FirstOrDefault(),
            Latest = new ReleaseInfo
            {
                Version = version,
                ReleaseId = item.Str("UIMD5") ?? $"{version}@{dateMs}",
                DownloadUrl = item.Str("UIDownload")!,
                FileName = fileName,
                Date = dateMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
                Changelog = Text.HtmlToText(item.Str("UIChangeLog")),
                FlavorMatched = matched,
            },
        };
    }
}

public sealed class WagoProvider : IAddonProvider
{
    public string Key => "wago";
    public string DisplayName => "Wago Addons";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        if (!url.Host.Equals("addons.wago.io", StringComparison.OrdinalIgnoreCase)) return false;
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(seg, s => s.Equals("addons", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= seg.Length) return false;
        projectId = seg[i + 1];
        return true;
    }

    public string WebsiteFor(string projectId) => $"https://addons.wago.io/addons/{projectId}";

    public const string PatreonUrl = "https://addons.wago.io/patreon";

    public const string KeyRejectedMessage =
        "Wago rejected the API key (401). Wago only accepts paid Patreon API keys - normal Wago keys don't work.";

    internal static async Task<JsonDocument> GetJsonAsync(string url, string apiKey, CancellationToken ct)
    {
        try
        {
            return await Http.GetJsonAsync(url, ct, r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey)).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex.IsAuthFailure)
        {
            throw new ProviderException(KeyRejectedMessage, ex.StatusCode);
        }
    }

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        var key = ctx.Settings.WagoApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new ProviderException("Wago Addons needs a paid Patreon API key (Settings -> API keys). Tip: most Wago addons are also on CurseForge, GitHub or WoWInterface - use one of those links instead.");

        var url = $"https://addons.wago.io/api/external/addons/{Uri.EscapeDataString(projectId)}?game_version={ctx.Flavor.WagoGameVersion}";
        using var doc = await GetJsonAsync(url, key!, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        var recent = root.Prop("recent_release");
        if (recent.ValueKind != JsonValueKind.Object) recent = root.Prop("recent_releases");
        var candidates = new List<(JsonElement rel, bool pre)>();
        foreach (var (channel, pre) in new[] { ("stable", false), ("beta", true), ("alpha", true) })
        {
            var r = recent.Prop(channel);
            if (r.ValueKind == JsonValueKind.Object && (allowPrerelease || !pre)) candidates.Add((r, pre));
        }
        if (candidates.Count == 0)
            throw new ProviderException($"No {(allowPrerelease ? "" : "stable ")}release on Wago for {ctx.Flavor.DisplayName}.");

        var (rel, isPre) = candidates.OrderByDescending(c => c.rel.Date("created_at")).First();
        var link = rel.Str("download_link") ?? rel.Str("link") ?? throw new ProviderException("Wago returned no download link.");
        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = root.Str("id") ?? projectId,
            Name = root.Str("display_name") ?? root.Str("name") ?? projectId,
            WebsiteUrl = root.Str("website_url") ?? WebsiteFor(projectId),
            Author = root.Arr("authors").Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : a.Str("name")).FirstOrDefault(),
            Summary = root.Str("summary"),
            Latest = new ReleaseInfo
            {
                Version = rel.Str("label") ?? rel.Str("id") ?? "",
                ReleaseId = rel.Str("id") ?? rel.Str("label") ?? "",
                DownloadUrl = link,
                Date = rel.Date("created_at"),
                Changelog = Text.HtmlToText(rel.Str("changelog")),
                IsPrerelease = isPre,
                FlavorMatched = true,
                DownloadHeaders = new() { ["Authorization"] = "Bearer " + key },
            },
        };
    }
}

public sealed class TukuiProvider : IAddonProvider
{
    public string Key => "tukui";
    public string DisplayName => "Tukui";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        if (!url.Host.EndsWith("tukui.org", StringComparison.OrdinalIgnoreCase)) return false;
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        projectId = q["ui"] ?? q["id"] ?? seg.LastOrDefault(s => !s.Equals("addons", StringComparison.OrdinalIgnoreCase) && !s.EndsWith(".php")) ?? "";
        return projectId.Length > 0;
    }

    public string WebsiteFor(string projectId) => $"https://tukui.org/{projectId}";

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync($"https://api.tukui.org/v1/addon/{Uri.EscapeDataString(projectId)}", ct).ConfigureAwait(false);
        var a = doc.RootElement;
        var version = a.Str("version") ?? "";
        var patches = a.Arr("patch").Select(p => p.GetString() ?? "").ToList();
        var prefix = ctx.Flavor.GameVersionPrefix;
        string? changelog = null;
        if (a.Str("changelog_url") is { } clUrl)
        {
            try { changelog = await Http.GetStringAsync(clUrl, ct).ConfigureAwait(false); } catch { }
        }
        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = a.Str("slug") ?? projectId,
            Name = a.Str("name") ?? projectId,
            WebsiteUrl = a.Str("web_url") ?? WebsiteFor(projectId),
            Author = a.Str("author"),
            Summary = a.Str("small_desc"),
            Latest = new ReleaseInfo
            {
                Version = version,
                ReleaseId = version,
                DownloadUrl = a.Str("url") ?? throw new ProviderException("Tukui returned no download link."),
                FileName = $"{a.Str("slug") ?? projectId}-{version}.zip",
                Date = a.Date("last_update"),
                Changelog = changelog,
                FlavorMatched = prefix != null && patches.Any(p => p.StartsWith(prefix)),
            },
        };
    }
}

public sealed class GitLabProvider : IAddonProvider
{
    public string Key => "gitlab";
    public string DisplayName => "GitLab";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        if (!url.Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase)) return false;
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).TakeWhile(s => s != "-").ToArray();
        if (seg.Length < 2) return false;
        if (seg[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)) seg[^1] = seg[^1][..^4];
        projectId = string.Join('/', seg);
        return true;
    }

    public string WebsiteFor(string projectId) => $"https://gitlab.com/{projectId}";

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        var api = $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(projectId)}";
        using var projDoc = await Http.GetJsonAsync(api, ct).ConfigureAwait(false);
        var proj = projDoc.RootElement;
        var result = new RemoteAddon
        {
            Provider = Key,
            ProjectId = projectId,
            Name = proj.Str("name") ?? projectId.Split('/').Last(),
            WebsiteUrl = proj.Str("web_url") ?? WebsiteFor(projectId),
            Author = proj.Prop("namespace").Str("name"),
            Summary = proj.Str("description"),
        };

        JsonDocument? relDoc = null;
        try { relDoc = await Http.GetJsonAsync($"{api}/releases?per_page=10", ct).ConfigureAwait(false); }
        catch (ProviderException) { }
        using var relDocScope = relDoc;
        var rel = relDoc == null ? default : relDoc.RootElement.Arr().FirstOrDefault(r => allowPrerelease || !r.Bool("upcoming_release"));
        if (rel.ValueKind == JsonValueKind.Object)
        {
            var tag = rel.Str("tag_name") ?? "";
            var links = rel.Prop("assets").Arr("links").ToList();
            var link = AssetPicker.Pick(links, l => l.Str("name") ?? l.Str("url") ?? "", ctx.Flavor, out var matched);
            var url = link.ValueKind == JsonValueKind.Object ? link.Str("direct_asset_url") ?? link.Str("url") : null;
            url ??= rel.Prop("assets").Arr("sources").FirstOrDefault(s => s.Str("format") == "zip").Str("url");
            if (url != null)
            {
                result.Latest = new ReleaseInfo
                {
                    Version = tag,
                    ReleaseId = tag + "|" + url,
                    DownloadUrl = url,
                    FileName = link.ValueKind == JsonValueKind.Object ? link.Str("name") : tag + ".zip",
                    Date = rel.Date("released_at"),
                    Changelog = rel.Str("description"),
                    FlavorMatched = matched,
                };
                return result;
            }
        }

        var branch = proj.Str("default_branch") ?? "main";
        using var commits = await Http.GetJsonAsync($"{api}/repository/commits?ref_name={Uri.EscapeDataString(branch)}&per_page=1", ct).ConfigureAwait(false);
        var c = commits.RootElement.Arr().FirstOrDefault();
        var sha = c.Str("id") ?? throw new ProviderException("GitLab project has no releases and no commits.");
        result.Latest = new ReleaseInfo
        {
            Version = $"{branch}@{sha[..7]}",
            ReleaseId = sha,
            DownloadUrl = $"{api}/repository/archive.zip?sha={sha}",
            FileName = $"{result.Name}-{sha[..7]}.zip",
            Date = c.Date("committed_date"),
            Changelog = "No releases published - following the latest commit:\n\n" + c.Str("message"),
        };
        return result;
    }
}

public sealed class WowUpHubProvider : IAddonProvider
{
    public string Key => "wowup";
    public string DisplayName => "WowUp Hub";

    private static readonly Dictionary<string, string[]> GameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["forever"] = new[] { "forever", "camelot" },
        ["mainline"] = new[] { "retail", "mainline" },
        ["retail"] = new[] { "retail", "mainline" },
        ["classic"] = new[] { "classic", "vanilla" },
        ["bcc"] = new[] { "burningCrusade", "bcc", "tbc" },
        ["wrath"] = new[] { "wotlk", "wrath" },
        ["cata"] = new[] { "cata" },
        ["mists"] = new[] { "mists", "mop" },
    };

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        if (!url.Host.EndsWith("wowup.io", StringComparison.OrdinalIgnoreCase)) return false;
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(seg, s => s.Equals("addons", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= seg.Length || !long.TryParse(seg[i + 1], out _)) return false;
        projectId = seg[i + 1];
        return true;
    }

    public string WebsiteFor(string projectId) => $"https://wowup.io/addons/{projectId}";

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        using var doc = await Http.GetJsonAsync($"https://hub.wowup.io/addons/{projectId}", ct).ConfigureAwait(false);
        var a = doc.RootElement.Prop("addon");
        if (a.ValueKind != JsonValueKind.Object) throw new ProviderException("WowUp Hub returned no addon.");

        var wanted = ctx.Flavor.ReleaseFlavors.SelectMany(f => GameTypes.TryGetValue(f, out var g) ? g : new[] { f })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var releases = a.Arr("releases").Where(r => allowPrerelease || !r.Bool("prerelease")).ToList();

        JsonElement? match = null;
        JsonElement gv = default;
        foreach (var r in releases)
        {
            gv = r.Arr("game_versions").FirstOrDefault(g => wanted.Contains(g.Str("game_type") ?? ""));
            if (gv.ValueKind == JsonValueKind.Object) { match = r; break; }
        }
        var flavorMatched = match != null;
        var rel = match ?? releases.FirstOrDefault();
        if (rel.ValueKind != JsonValueKind.Object) throw new ProviderException("No release found on WowUp Hub.");

        var tocVersion = flavorMatched ? gv.Str("version") : null;
        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = projectId,
            Name = a.Str("repository_name") ?? projectId,
            WebsiteUrl = a.Str("repository") ?? WebsiteFor(projectId),
            Author = a.Str("owner_name"),
            Summary = Text.HtmlToText(a.Str("description"))?.Split('\n').FirstOrDefault(l => l.Length > 20),
            Latest = new ReleaseInfo
            {
                Version = string.IsNullOrWhiteSpace(tocVersion) ? rel.Str("tag_name") ?? "" : tocVersion!,
                ReleaseId = rel.Str("id") ?? rel.Str("external_id") ?? "",
                DownloadUrl = rel.Str("download_url") ?? throw new ProviderException("WowUp Hub release has no download."),
                FileName = rel.Str("name"),
                Date = rel.Date("published_at"),
                Changelog = Text.HtmlToText(rel.Str("body")),
                IsPrerelease = rel.Bool("prerelease"),
                FlavorMatched = flavorMatched,
            },
        };
    }
}

public sealed class DirectUrlProvider : IAddonProvider
{
    public string Key => "url";
    public string DisplayName => "Direct link";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = url.AbsoluteUri;
        return url.Scheme is "http" or "https";
    }

    public string WebsiteFor(string projectId) => projectId;

    public async Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        HttpResponseMessage resp;
        using (var head = new HttpRequestMessage(HttpMethod.Head, projectId))
            resp = await Http.Client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            using var get = new HttpRequestMessage(HttpMethod.Get, projectId);
            resp = await Http.Client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        using (resp)
        {
            await Http.EnsureOk(resp, projectId, ct).ConfigureAwait(false);
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
                throw new ProviderException("This link opens a web page, not a .zip file. Use the addon's page on a supported site (GitHub, CurseForge, WoWInterface, Wago, GitLab, Tukui) or a direct download link.");

            var etag = resp.Headers.ETag?.Tag;
            var modified = resp.Content.Headers.LastModified;
            var size = resp.Content.Headers.ContentLength;
            var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
                           ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? Path.GetFileName(new Uri(projectId).AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "addon.zip";

            var releaseId = etag ?? modified?.ToUnixTimeSeconds().ToString() ?? size?.ToString() ?? "";
            var fromName = VersionComparer.NumericParts(fileName) is { } parts ? string.Join('.', parts) : null;
            var version = fromName ?? modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "latest";
            AssetPicker.Score(fileName, ctx.Flavor, out var matched);
            return new RemoteAddon
            {
                Provider = Key,
                ProjectId = projectId,
                Name = Path.GetFileNameWithoutExtension(fileName),
                WebsiteUrl = projectId,
                Latest = new ReleaseInfo
                {
                    Version = version,
                    ReleaseId = releaseId,
                    DownloadUrl = projectId,
                    FileName = fileName,
                    Date = modified,
                    Size = size,
                    FlavorMatched = matched,
                },
            };
        }
    }
}
