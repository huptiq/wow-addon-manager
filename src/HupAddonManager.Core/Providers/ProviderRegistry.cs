using System.Text.RegularExpressions;

namespace HupAddonManager.Core.Providers;

public sealed partial class ProviderRegistry
{
    public IReadOnlyList<IAddonProvider> All { get; }

    public ProviderRegistry()
    {
        All = new IAddonProvider[]
        {
            new GitHubProvider(),
            new CurseForgeProvider(),
            new WagoProvider(),
            new WoWInterfaceProvider(),
            new GitLabProvider(),
            new TukuiProvider(),
            new WowUpHubProvider(),
            new DirectUrlProvider(),
        };
    }

    public IAddonProvider? Get(string key) => All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex OwnerRepo();

    public (IAddonProvider Provider, string ProjectId) Parse(string input)
    {
        var text = (input ?? "").Trim().Trim('"', '\'', '<', '>');
        if (text.Length == 0) throw new ProviderException("Paste a link to an addon first.");

        if (OwnerRepo().IsMatch(text) && !text.Contains(".com") && !text.Contains(".io") && !text.Contains(".org"))
            return (Get("github")!, text);

        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https"))
            throw new ProviderException("That doesn't look like a web link.");

        var host = url.Host.ToLowerInvariant();
        if (host is "wago.io" or "www.wago.io")
            throw new ProviderException("wago.io links are WeakAura/Plater import strings, not addons. For addons use an addons.wago.io link (or the addon's CurseForge/GitHub page).");
        if (host.EndsWith("curseforge.com") && !url.AbsolutePath.Contains("/wow/", StringComparison.OrdinalIgnoreCase) && !url.AbsolutePath.Contains("/projects/", StringComparison.OrdinalIgnoreCase))
            throw new ProviderException("That CurseForge link isn't a WoW addon page (expected ...curseforge.com/wow/addons/name).");

        foreach (var p in All)
        {
            if (p is DirectUrlProvider) continue;
            if (p.TryParse(url, out var id)) return (p, id);
        }

        if (host.EndsWith("github.com") || host.EndsWith("curseforge.com") || host.EndsWith("wowinterface.com") || host.EndsWith("wago.io") || host.EndsWith("gitlab.com"))
            throw new ProviderException("Couldn't find the addon in that link. Open the addon's main page and copy that link.");

        var direct = Get("url")!;
        direct.TryParse(url, out var directId);
        return (direct, directId);
    }
}
