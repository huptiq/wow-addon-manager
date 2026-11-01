using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HupAddonManager.Core.Providers;

public static partial class WagoWeb
{
    public const string Site = "https://addons.wago.io";

    public sealed record Listing(string Slug, string Name, string? Summary, string? Author, long Downloads, DateTimeOffset? Updated)
    {
        public string Link => $"{Site}/addons/{Slug}";
    }

    [GeneratedRegex("data-page=\"([^\"]*)\"")]
    private static partial Regex DataPage();
    [GeneratedRegex(@"href=""https://addons\.wago\.io/addons/([^""/?#]+)""")]
    private static partial Regex SlugRx();
    [GeneratedRegex(@"<h3[^>]*>([^<]*)</h3>")]
    private static partial Regex NameRx();
    [GeneratedRegex(@"<p[^>]*>([^<]*)</p>")]
    private static partial Regex SummaryRx();
    [GeneratedRegex(@"<strong>Updated:</strong>\s*([^<]+)<")]
    private static partial Regex UpdatedRx();
    [GeneratedRegex(@"<strong>Downloads:</strong>\s*([\d,.]+)")]
    private static partial Regex DownloadsRx();
    [GeneratedRegex(@"<strong>Author:</strong>\s*([^<]+)<")]
    private static partial Regex AuthorRx();

    internal static JsonDocument ParsePage(string html)
    {
        var m = DataPage().Match(html);
        if (!m.Success) throw new ProviderException("Couldn't read the Wago website (its page layout may have changed).");
        return JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
    }

    private static async Task<JsonDocument> GetPageAsync(string url, CancellationToken ct) =>
        ParsePage(await Http.GetStringAsync(url, ct).ConfigureAwait(false));

    internal static Listing? ParseCard(string card)
    {
        var slug = SlugRx().Match(card);
        if (!slug.Success) return null;
        static string? Grab(Regex rx, string s) => rx.Match(s) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
        var name = Grab(NameRx(), card);
        var downloads = Grab(DownloadsRx(), card)?.Replace(",", "").Replace(".", "");
        var updated = Grab(UpdatedRx(), card);
        return new Listing(
            slug.Groups[1].Value,
            string.IsNullOrEmpty(name) ? slug.Groups[1].Value : name,
            Grab(SummaryRx(), card),
            Grab(AuthorRx(), card),
            long.TryParse(downloads, out var d) ? d : 0,
            DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var u) ? u : null);
    }

    public static async Task<List<Listing>> SearchAsync(string? text, string gameVersion, CancellationToken ct)
    {
        var url = $"{Site}/?game_version={Uri.EscapeDataString(gameVersion)}";
        if (!string.IsNullOrWhiteSpace(text)) url += "&search=" + Uri.EscapeDataString(text.Trim());
        using var doc = await GetPageAsync(url, ct).ConfigureAwait(false);
        var props = doc.RootElement.Prop("props");
        return props.Prop("addons").Arr("data")
            .Select(c => c.ValueKind == JsonValueKind.String ? ParseCard(c.GetString() ?? "") : null)
            .OfType<Listing>()
            .ToList();
    }

    public static bool IsWagoLink(string? link) =>
        Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var u) && u.Host.Equals("addons.wago.io", StringComparison.OrdinalIgnoreCase);

    public sealed record AddonPage(string Name, List<string> Links, Dictionary<string, string> ReleaseLabels)
    {
        public bool KnowsReleases => ReleaseLabels.Count > 0;

        public string? LabelFor(string gameVersion) => ReleaseLabels.TryGetValue(gameVersion, out var l) ? l : null;
    }

    public static async Task<AddonPage> GetAddonPageAsync(string slug, CancellationToken ct)
    {
        using var doc = await GetPageAsync($"{Site}/addons/{Uri.EscapeDataString(slug)}", ct).ConfigureAwait(false);
        return ParseAddonPage(doc.RootElement.Prop("props"), slug);
    }

    internal static AddonPage ParseAddonPage(JsonElement props, string slug)
    {
        var addon = props.Prop("addon");
        var name = WebUtility.HtmlDecode(addon.Str("display_name") ?? slug);
        var links = new[] { addon.Str("website"), addon.Str("source_url") }
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var recent = props.Prop("metadata").Prop("recent_releases");
        if (recent.ValueKind == JsonValueKind.Object)
        {
            foreach (var channel in recent.EnumerateObject())
            {
                if (channel.Value.Str("label") is { Length: > 0 } label) labels[channel.Name] = WebUtility.HtmlDecode(label);
            }
        }
        return new AddonPage(name, links, labels);
    }
}
