using System.Net;
using System.Text.Json;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core.Providers;

public sealed class CurseForgeProvider : IAddonProvider
{
    public string Key => "curseforge";
    public string DisplayName => "CurseForge";

    public bool TryParse(Uri url, out string projectId)
    {
        projectId = "";
        var host = url.Host.ToLowerInvariant();
        if (!host.EndsWith("curseforge.com")) return false;
        var seg = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(seg, s => s.Equals("addons", StringComparison.OrdinalIgnoreCase) || s.Equals("projects", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < seg.Length) { projectId = seg[i + 1]; return true; }
        return false;
    }

    public string WebsiteFor(string projectId) =>
        long.TryParse(projectId, out _)
            ? $"https://www.curseforge.com/projects/{projectId}"
            : $"https://www.curseforge.com/wow/addons/{projectId}";

    public Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        var key = ctx.Settings.CurseForgeApiKey;
        return string.IsNullOrWhiteSpace(key)
            ? ViaCfWidgetAsync(projectId, allowPrerelease, ctx, ct)
            : ViaOfficialApiAsync(projectId, allowPrerelease, ctx, key!, ct);
    }

    public static string CdnUrl(long fileId, string fileName) =>
        $"https://edge.forgecdn.net/files/{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(fileName)}";

    private static bool IsForFlavor(IEnumerable<string> gameVersions, WowFlavor flavor)
    {
        var prefix = flavor.GameVersionPrefix;
        foreach (var v in gameVersions)
        {
            if (prefix != null && (v == prefix || v.StartsWith(prefix + ".", StringComparison.Ordinal))) return true;
            if (flavor.ReleaseFlavors.Any(f => v.Contains(f, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private sealed record CfFile(long Id, string Display, string FileName, int ReleaseType, DateTimeOffset? Date, long? Size, List<string> Versions, string? Url);

    private static CfFile? Choose(IEnumerable<CfFile> files, bool allowPrerelease, WowFlavor flavor, out bool matched)
    {
        var list = files.Where(f => allowPrerelease || f.ReleaseType == 1).OrderByDescending(f => f.Date).ToList();
        var forFlavor = list.FirstOrDefault(f => IsForFlavor(f.Versions, flavor));
        matched = forFlavor != null;
        return forFlavor ?? list.FirstOrDefault();
    }

    private async Task<RemoteAddon> ViaCfWidgetAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct)
    {
        var url = long.TryParse(projectId, out _)
            ? $"https://api.cfwidget.com/{projectId}"
            : $"https://api.cfwidget.com/wow/addons/{Uri.EscapeDataString(projectId)}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.Client.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Accepted)
            throw new ProviderException("CurseForge data is being prepared by CFWidget. Try again in a minute (or add a CurseForge API key in Settings).");
        await Http.EnsureOk(resp, url, ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.Str("error") is { } err) throw new ProviderException("CurseForge (CFWidget): " + err);

        var files = root.Arr("files").Select(f => new CfFile(
            f.Long("id") ?? 0,
            f.Str("display") ?? f.Str("name") ?? "",
            f.Str("name") ?? "",
            (f.Str("type") ?? "release").ToLowerInvariant() switch { "beta" => 2, "alpha" => 3, _ => 1 },
            f.Date("uploaded_at"),
            f.Long("filesize"),
            f.Arr("versions").Select(v => v.GetString() ?? "").ToList(),
            null)).Where(f => f.Id > 0).ToList();

        var chosen = Choose(files, allowPrerelease, ctx.Flavor, out var matched)
                     ?? throw new ProviderException("No downloadable files found on CurseForge.");

        var id = root.Long("id")?.ToString() ?? projectId;
        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = id,
            Name = root.Str("title") ?? projectId,
            WebsiteUrl = root.Prop("urls").Str("curseforge") ?? WebsiteFor(projectId),
            Author = root.Arr("members").Select(m => m.Str("username")).FirstOrDefault(),
            Summary = root.Str("summary"),
            Latest = ToRelease(chosen, matched, null),
        };
    }

    private async Task<RemoteAddon> ViaOfficialApiAsync(string projectId, bool allowPrerelease, ProviderContext ctx, string apiKey, CancellationToken ct)
    {
        const string api = "https://api.curseforge.com/v1";
        void Auth(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        if (!long.TryParse(projectId, out var modId))
        {
            using var search = await Http.GetJsonAsync($"{api}/mods/search?gameId=1&slug={Uri.EscapeDataString(projectId)}", ct, Auth).ConfigureAwait(false);
            var hit = search.RootElement.Arr("data").FirstOrDefault(m => string.Equals(m.Str("slug"), projectId, StringComparison.OrdinalIgnoreCase));
            modId = hit.ValueKind == JsonValueKind.Object ? hit.Long("id") ?? 0 : 0;
            if (modId == 0) throw new ProviderException($"CurseForge project \"{projectId}\" was not found.");
        }

        using var modDoc = await Http.GetJsonAsync($"{api}/mods/{modId}", ct, Auth).ConfigureAwait(false);
        var mod = modDoc.RootElement.Prop("data");
        using var filesDoc = await Http.GetJsonAsync($"{api}/mods/{modId}/files?pageSize=50", ct, Auth).ConfigureAwait(false);

        var files = filesDoc.RootElement.Arr("data")
            .Where(f => f.Prop("isAvailable").ValueKind != JsonValueKind.False)
            .Select(f => new CfFile(
                f.Long("id") ?? 0,
                f.Str("displayName") ?? f.Str("fileName") ?? "",
                f.Str("fileName") ?? "",
                (int)(f.Long("releaseType") ?? 1),
                f.Date("fileDate"),
                f.Long("fileLength"),
                f.Arr("gameVersions").Select(v => v.GetString() ?? "")
                    .Concat(f.Arr("sortableGameVersions").Select(s => s.Str("gameVersionName") ?? "")).ToList(),
                f.Str("downloadUrl")))
            .Where(f => f.Id > 0).ToList();

        var chosen = Choose(files, allowPrerelease, ctx.Flavor, out var matched)
                     ?? throw new ProviderException("No downloadable files found on CurseForge.");

        string? changelog = null;
        try
        {
            using var cl = await Http.GetJsonAsync($"{api}/mods/{modId}/files/{chosen.Id}/changelog", ct, Auth).ConfigureAwait(false);
            changelog = Text.HtmlToText(cl.RootElement.Str("data"));
        }
        catch { }

        return new RemoteAddon
        {
            Provider = Key,
            ProjectId = modId.ToString(),
            Name = mod.Str("name") ?? projectId,
            WebsiteUrl = mod.Prop("links").Str("websiteUrl") ?? WebsiteFor(projectId),
            Author = mod.Arr("authors").Select(a => a.Str("name")).FirstOrDefault(),
            Summary = mod.Str("summary"),
            Latest = ToRelease(chosen, matched, changelog),
        };
    }

    private static ReleaseInfo ToRelease(CfFile f, bool matched, string? changelog) => new()
    {
        Version = f.Display,
        ReleaseId = f.Id.ToString(),
        DownloadUrl = string.IsNullOrEmpty(f.Url) ? CdnUrl(f.Id, f.FileName) : f.Url!,
        FileName = f.FileName,
        Date = f.Date,
        Size = f.Size,
        IsPrerelease = f.ReleaseType != 1,
        FlavorMatched = matched,
        Changelog = changelog,
    };
}
