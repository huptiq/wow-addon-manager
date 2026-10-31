using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core.Providers;

public sealed class ProviderException(string message) : Exception(message);

public sealed class ProviderContext
{
    public required AppSettings Settings { get; init; }
    public required WowFlavor Flavor { get; init; }
}

public interface IAddonProvider
{
    string Key { get; }
    string DisplayName { get; }

    bool TryParse(Uri url, out string projectId);

    string WebsiteFor(string projectId);

    Task<RemoteAddon> GetLatestAsync(string projectId, bool allowPrerelease, ProviderContext ctx, CancellationToken ct);
}

public static class Http
{
    private static readonly Lazy<HttpClient> LazyClient = new(() =>
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HupAddonManager/{AppInfo.VersionText}");
        return c;
    });

    public static HttpClient Client => LazyClient.Value;

    public static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(req);
        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOk(resp, url, ct).ConfigureAwait(false);
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(req);
        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOk(resp, url, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    public static async Task EnsureOk(HttpResponseMessage resp, string url, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var host = new Uri(url).Host;
        var code = (int)resp.StatusCode;
        string detail = "";
        try { detail = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); } catch { }
        if (detail.Length > 160) detail = detail[..160] + "...";

        throw resp.StatusCode switch
        {
            HttpStatusCode.NotFound => new ProviderException($"Not found on {host} (404). Check the link."),
            HttpStatusCode.Unauthorized => new ProviderException($"{host} rejected the request (401). Check the API key in Settings."),
            HttpStatusCode.Forbidden when host.Contains("github") && resp.Headers.TryGetValues("x-ratelimit-remaining", out var rl) && rl.FirstOrDefault() == "0"
                => new ProviderException("GitHub rate limit reached (60 requests/hour without a token). Add a GitHub token in Settings or try again later."),
            HttpStatusCode.Forbidden => new ProviderException($"{host} refused access (403). {detail}"),
            HttpStatusCode.TooManyRequests => new ProviderException($"{host} is rate limiting us (429). Try again in a few minutes."),
            _ => new ProviderException($"{host} returned HTTP {code}. {detail}"),
        };
    }

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString(),
                JsonValueKind.Number => p.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    public static long? Long(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.Number when p.TryGetInt64(out var l) => l,
                JsonValueKind.String when long.TryParse(p.GetString(), out var l2) => l2,
                _ => null,
            }
            : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    public static JsonElement Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) ? p : default;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string? name = null)
    {
        var a = name == null ? e : e.Prop(name);
        return a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        DateTimeOffset.TryParse(e.Str(name), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

public static partial class Text
{
    [GeneratedRegex("<br\\s*/?>|</p>|</li>|</h\\d>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTags();
    [GeneratedRegex("<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();
    [GeneratedRegex("\n{3,}")]
    private static partial Regex ManyNewlines();

    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = html.Replace("\r", "");
        s = ListItem().Replace(s, "• ");
        s = LineBreakTags().Replace(s, "\n");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = ManyNewlines().Replace(s, "\n\n");
        return s.Trim();
    }
}
