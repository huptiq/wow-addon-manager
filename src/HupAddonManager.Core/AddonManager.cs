using System.Text.Json;
using HupAddonManager.Core.Install;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Providers;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.Toc;
using HupAddonManager.Core.Update;
using HupAddonManager.Core.Wow;

namespace HupAddonManager.Core;

public sealed class AddonEntry
{
    public TrackedAddon? Tracked { get; init; }
    public List<TocInfo> Folders { get; init; } = new();
    public string Name { get; init; } = "";
    public string? InstalledVersion { get; init; }
    public string? LatestVersion => Tracked?.Latest?.Version;
    public UpdateStatus Status { get; init; }
    public bool? Compatible { get; init; }
    public string? Author { get; init; }
    public string? Notes { get; init; }
    public string Key => Tracked?.Id ?? "folder:" + (Folders.FirstOrDefault()?.FolderName ?? Name);
    public IEnumerable<string> FolderNames => Tracked?.Folders ?? Folders.Select(f => f.FolderName);
}

public sealed class UpdateSummary
{
    public List<string> Updated { get; } = new();
    public List<(string Name, string Error)> Failed { get; } = new();
    public List<string> Available { get; } = new();
    public bool SkippedBecauseWowRunning { get; set; }
}

public sealed class AddonManager
{
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly object _dbGate = new();

    public AppSettings Settings { get; private set; }
    public AddonDatabase Db { get; }
    public ProviderRegistry Providers { get; } = new();
    public Search.AddonSearch Search { get; } = new();
    public WowFlavor? Flavor { get; private set; }
    public List<WowFlavor> Flavors { get; private set; } = new();
    public IReadOnlyList<AddonEntry> Entries { get; private set; } = Array.Empty<AddonEntry>();

    public event Action? Changed;
    public event Action<string, double?>? Progress;

    public AddonManager()
    {
        Settings = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        Db = JsonStore.Load<AddonDatabase>(AppPaths.DatabaseFile);
        if (Db.Flavors.Comparer != StringComparer.OrdinalIgnoreCase)
            Db.Flavors = new Dictionary<string, FlavorData>(Db.Flavors, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsBusy => _opLock.CurrentCount == 0;

    public void SaveSettings() => JsonStore.Save(AppPaths.SettingsFile, Settings);

    public void SaveDb()
    {
        lock (_dbGate) JsonStore.Save(AppPaths.DatabaseFile, Db);
    }

    private void Report(string text, double? fraction = null) => Progress?.Invoke(text, fraction);
    private void RaiseChanged() => Changed?.Invoke();

    private ProviderContext Ctx => new() { Settings = Settings, Flavor = Flavor ?? throw new InvalidOperationException("No WoW folder selected.") };
    private FlavorData Data => Db.For(Flavor!.Key);

    public bool InitializeWow()
    {
        if (!WowLocator.IsWowRoot(Settings.WowRoot))
        {
            var found = WowLocator.FindRoots().FirstOrDefault();
            if (found == null) return false;
            Settings.WowRoot = found;
            SaveSettings();
            Log.Info($"WoW folder detected: {found}");
        }
        Flavors = WowLocator.GetFlavors(Settings.WowRoot!);
        Flavor = Flavors.FirstOrDefault(f => f.Key.Equals(Settings.SelectedFlavor, StringComparison.OrdinalIgnoreCase))
                 ?? Flavors.FirstOrDefault();
        if (Flavor == null) return false;
        Installer.CleanupWorkDir(Flavor.AddOnsPath);
        return true;
    }

    public bool SetWowRoot(string path)
    {
        if (!WowLocator.IsWowRoot(path)) return false;
        Settings.WowRoot = path;
        SaveSettings();
        return InitializeWow();
    }

    public void SelectFlavor(string key)
    {
        var f = Flavors.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (f == null) return;
        Flavor = f;
        Settings.SelectedFlavor = f.Key;
        SaveSettings();
        Installer.CleanupWorkDir(f.AddOnsPath);
    }

    public void Scan()
    {
        if (Flavor == null) { Entries = Array.Empty<AddonEntry>(); RaiseChanged(); return; }
        var flavor = Flavor;
        var tocs = new Dictionary<string, TocInfo>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(flavor.AddOnsPath))
        {
            foreach (var dir in Directory.GetDirectories(flavor.AddOnsPath))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.Equals(Installer.WorkDirName, StringComparison.OrdinalIgnoreCase)) continue;
                var toc = TocParser.ReadFolder(dir, flavor.TocSuffixes);
                if (toc != null) tocs[name] = toc;
            }
        }

        var data = Data;
        var entries = new List<AddonEntry>();
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_dbGate)
        {
            foreach (var a in data.Addons) foreach (var f in a.Folders) owned.Add(f);

            var unowned = tocs.Keys.Where(k => !owned.Contains(k)).ToList();
            var groups = GroupFolders(unowned, tocs);
            var autoLinked = false;
            foreach (var g in groups)
            {
                var main = tocs[g[0]];
                if (data.NoAutoLink.Contains(main.FolderName, StringComparer.OrdinalIgnoreCase)) continue;
                var hint = DetectSource(main);
                if (hint == null) continue;
                var existing = data.Addons.FirstOrDefault(a => a.Provider == hint.Value.Provider && a.ProjectId.Equals(hint.Value.ProjectId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Folders.AddRange(g.Where(f => !existing.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)));
                }
                else
                {
                    data.Addons.Add(new TrackedAddon
                    {
                        Name = main.DisplayName,
                        Provider = hint.Value.Provider,
                        ProjectId = hint.Value.ProjectId,
                        SourceUrl = Providers.Get(hint.Value.Provider)!.WebsiteFor(hint.Value.ProjectId),
                        Folders = g.ToList(),
                        InstalledVersion = main.Version,
                        AutoDetected = true,
                    });
                    Log.Info($"Auto-linked {main.DisplayName} -> {hint.Value.Provider}:{hint.Value.ProjectId}");
                }
                foreach (var f in g) owned.Add(f);
                autoLinked = true;
            }
            if (autoLinked) SaveDb();

            foreach (var a in data.Addons)
            {
                var found = a.Folders.Where(tocs.ContainsKey).Select(f => tocs[f]).ToList();
                var main = PickMain(found, a.Name);
                entries.Add(new AddonEntry
                {
                    Tracked = a,
                    Folders = found,
                    Name = a.Name,
                    InstalledVersion = a.InstalledVersion ?? main?.Version,
                    Status = StatusOf(a, found.Count > 0),
                    Compatible = main == null ? null : IsCompatible(main, flavor),
                    Author = main?.Author,
                    Notes = main?.Notes,
                });
            }

            foreach (var g in GroupFolders(tocs.Keys.Where(k => !owned.Contains(k)).ToList(), tocs))
            {
                var found = g.Select(f => tocs[f]).ToList();
                var main = found[0];
                entries.Add(new AddonEntry
                {
                    Folders = found,
                    Name = main.DisplayName,
                    InstalledVersion = main.Version,
                    Status = UpdateStatus.Unlinked,
                    Compatible = IsCompatible(main, flavor),
                    Author = main.Author,
                    Notes = main.Notes,
                });
            }
        }

        Entries = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
        RaiseChanged();
    }

    private static TocInfo? PickMain(List<TocInfo> found, string name) =>
        found.FirstOrDefault(t => t.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? found.OrderBy(t => t.FolderName.Length).FirstOrDefault();

    public static UpdateStatus StatusOf(TrackedAddon a, bool hasFolders)
    {
        if (!hasFolders) return UpdateStatus.Missing;
        if (a.LastError != null) return UpdateStatus.Error;
        if (a.Latest == null) return UpdateStatus.NotChecked;
        return VersionComparer.StatusFor(a, a.Latest);
    }

    public static bool? IsCompatible(TocInfo toc, WowFlavor flavor)
    {
        if (toc.IsFlavorToc) return true;
        var expected = flavor.InterfaceVersion;
        if (expected == null || toc.Interfaces.Count == 0) return null;
        return toc.Interfaces.Any(i => i / 100 == expected.Value / 100);
    }

    internal static List<List<string>> GroupFolders(List<string> folders, IReadOnlyDictionary<string, TocInfo> tocs)
    {
        var byLength = folders.OrderBy(f => f.Length).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in byLength)
        {
            var p = byLength.FirstOrDefault(c => c.Length < f.Length && f.StartsWith(c, StringComparison.OrdinalIgnoreCase)
                                                 && IsBoundary(f[c.Length]) && !parent.ContainsKey(c));
            p ??= SeparatorParent(f, byLength, parent);
            if (p == null) continue;
            var own = tocs.TryGetValue(f, out var t) ? SourceKey(t) : null;
            var parents = tocs.TryGetValue(p, out var pt) ? SourceKey(pt) : null;
            if (own != null && own != parents) continue;
            parent[f] = p;
        }

        var groups = new List<List<string>>();
        foreach (var f in byLength.Where(f => !parent.ContainsKey(f)))
            groups.Add(new List<string> { f });
        foreach (var (child, p) in parent)
            groups.First(g => g[0].Equals(p, StringComparison.OrdinalIgnoreCase)).Add(child);
        return groups;

        static bool IsBoundary(char c) => c is '_' or '-' or '.' || char.IsUpper(c) || char.IsDigit(c);
    }

    private static string? SeparatorParent(string f, List<string> all, Dictionary<string, string> parent)
    {
        var dash = f.IndexOf('-');
        if (dash <= 0) return null;
        var prefix = f[..(dash + 1)];
        var siblings = all.Where(o => o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !parent.ContainsKey(o)).ToList();
        if (siblings.Count < 2) return null;
        var main = siblings.FirstOrDefault(s => s.EndsWith("-Core", StringComparison.OrdinalIgnoreCase)) ?? siblings.OrderBy(s => s.Length).First();
        return main.Equals(f, StringComparison.OrdinalIgnoreCase) ? null : main;
    }

    private static string? SourceKey(TocInfo t) =>
        t.Field("X-Curse-Project-ID") ?? t.Field("X-WoWI-ID") ?? t.Field("X-Wago-ID");

    public (string Provider, string ProjectId)? DetectSource(TocInfo toc)
    {
        (string, string)? FromUrl(string? field, params string[] allowed)
        {
            if (field == null) return null;
            try
            {
                var (p, id) = Providers.Parse(field);
                if (allowed.Contains(p.Key)) return (p.Key, id);
            }
            catch { }
            return null;
        }

        var urlFields = new[] { "X-Website", "X-Github", "X-GitHub-Repo", "X-Repository", "X-Repo", "X-URL", "X-Homepage", "URL" }
            .Select(toc.Field).Where(v => v != null).ToList();

        var hasCfKey = !string.IsNullOrWhiteSpace(Settings.CurseForgeApiKey);
        var cf = toc.Field("X-Curse-Project-ID") is { } cfId && long.TryParse(cfId, out _) ? ("curseforge", cfId) : ((string, string)?)null;
        if (hasCfKey && cf != null) return cf;

        foreach (var u in urlFields) if (FromUrl(u, "github") is { } gh) return gh;
        if (cf != null) return cf;
        if (toc.Field("X-WoWI-ID") is { } wowi && long.TryParse(wowi, out _)) return ("wowinterface", wowi);
        if (!string.IsNullOrWhiteSpace(Settings.WagoApiKey) && toc.Field("X-Wago-ID") is { } wago) return ("wago", wago);
        if (toc.Field("X-Tukui-ProjectID") is { } tk) { if (tk == "-1") return ("tukui", "tukui"); if (tk == "-2") return ("tukui", "elvui"); }
        foreach (var u in urlFields) if (FromUrl(u, "curseforge", "wowinterface", "gitlab", "tukui", "wowup") is { } other) return other;
        return null;
    }

    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        if (Flavor == null) return;
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try { await CheckAllCoreAsync(ct).ConfigureAwait(false); }
        finally { _opLock.Release(); Report("Ready"); }
    }

    private async Task CheckAllCoreAsync(CancellationToken ct)
    {
        Scan();
        List<TrackedAddon> list;
        lock (_dbGate) list = Data.Addons.ToList();
        if (list.Count == 0) return;

        var done = 0;
        using var throttle = new SemaphoreSlim(4);
        var tasks = list.Select(async a =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try { await CheckOneCoreAsync(a, ct).ConfigureAwait(false); }
            finally
            {
                throttle.Release();
                var n = Interlocked.Increment(ref done);
                Report($"Checking for updates... {n}/{list.Count}", (double)n / list.Count);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        SaveDb();
        Scan();
        Log.Info($"Update check finished: {Entries.Count(e => e.Status == UpdateStatus.UpdateAvailable)} update(s) available");
    }

    public async Task CheckOneAsync(TrackedAddon a, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Report($"Checking {a.Name}...");
            await CheckOneCoreAsync(a, ct).ConfigureAwait(false);
            SaveDb();
            Scan();
        }
        finally { _opLock.Release(); Report("Ready"); }
    }

    private async Task CheckOneCoreAsync(TrackedAddon a, CancellationToken ct)
    {
        var provider = Providers.Get(a.Provider);
        if (provider == null) { a.LastError = $"Unknown source \"{a.Provider}\""; return; }
        try
        {
            var remote = await provider.GetLatestAsync(a.ProjectId, a.AllowPrerelease, Ctx, ct).ConfigureAwait(false);
            lock (_dbGate)
            {
                a.Latest = remote.Latest;
                a.LastChecked = DateTimeOffset.Now;
                a.LastError = null;
                if (string.IsNullOrEmpty(a.InstalledReleaseId) && VersionComparer.Compare(a.InstalledVersion, remote.Latest.Version) == 0)
                    a.InstalledReleaseId = remote.Latest.ReleaseId;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (_dbGate)
            {
                a.LastError = ex is ProviderException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                a.LastChecked = DateTimeOffset.Now;
            }
            Log.Warn($"Check failed for {a.Name}: {a.LastError}");
        }
    }

    public async Task<RemoteAddon> LookupAsync(string link, bool allowPrerelease, CancellationToken ct = default)
    {
        var (provider, id) = Providers.Parse(link);
        if (provider is WagoProvider) return await LookupWagoAsync(provider, id, allowPrerelease, ct).ConfigureAwait(false);
        return await provider.GetLatestAsync(id, allowPrerelease, Ctx, ct).ConfigureAwait(false);
    }

    private async Task<RemoteAddon> LookupWagoAsync(IAddonProvider wago, string slug, bool allowPrerelease, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(Settings.WagoApiKey))
        {
            try { return await wago.GetLatestAsync(slug, allowPrerelease, Ctx, ct).ConfigureAwait(false); }
            catch (ProviderException ex) when (ex.IsAuthFailure)
            {
                Log.Info("Wago refused the API key - using the addon's own source instead.");
            }
        }

        var flavor = Flavor ?? throw new ProviderException("Pick a game client first.");
        var page = await WagoWeb.GetAddonPageAsync(slug, ct).ConfigureAwait(false);
        var name = page.Name;
        var wagoLabel = page.LabelFor(flavor.WagoGameVersion);
        if (page.KnowsReleases && wagoLabel == null)
            throw new ProviderException($"{name} has no version for {flavor.DisplayName} on Wago (it only lists: {string.Join(", ", page.ReleaseLabels.Keys)}).");

        var candidates = new List<(IAddonProvider Source, RemoteAddon Remote)>();
        string? lastError = null;
        foreach (var link in page.Links)
        {
            IAddonProvider source;
            string sourceId;
            try { (source, sourceId) = Providers.Parse(link); }
            catch (ProviderException) { continue; }
            if (source is WagoProvider or DirectUrlProvider || candidates.Any(c => c.Source == source)) continue;
            try
            {
                candidates.Add((source, await source.GetLatestAsync(sourceId, allowPrerelease, Ctx, ct).ConfigureAwait(false)));
            }
            catch (ProviderException ex)
            {
                lastError = ex.Message;
                Log.Warn($"{name}: source {link} from Wago didn't work: {ex.Message}");
            }
        }

        var pick = PickWagoCandidate(candidates.Select(c => c.Remote).ToList(), wagoLabel, flavor);
        if (pick != null)
        {
            var source = candidates.First(c => c.Remote == pick).Source;
            Log.Info($"{name}: installing {pick.Latest.Version} for {flavor.DisplayName} from {source.DisplayName} (linked on its Wago page).");
            return pick;
        }

        if (candidates.Count > 0)
        {
            var offered = string.Join(", ", candidates.Select(c => $"{c.Source.DisplayName}: {c.Remote.Latest.FileName ?? c.Remote.Latest.Version}"));
            throw new ProviderException(
                $"Couldn't find the {flavor.DisplayName} version of {name}. The sources linked on its Wago page only offer other game versions ({offered}).\n\n" +
                "Install it with the Wago App or from its Wago page, or add a Wago Patreon key in Settings -> API keys.");
        }

        throw new ProviderException(
            $"{name} is only downloadable from Wago itself, and Wago doesn't let other apps download its files without a paid Patreon API key. " +
            "Install it with the Wago App or from its Wago page, or add a Patreon key in Settings -> API keys." +
            (lastError != null ? $"\n\n(The source linked on its Wago page failed: {lastError})" : ""));
    }

    internal static RemoteAddon? PickWagoCandidate(List<RemoteAddon> candidates, string? wagoLabel, WowFlavor flavor)
    {
        bool SameAsWago(RemoteAddon r) => wagoLabel != null &&
            (r.Latest.Version.Contains(wagoLabel, StringComparison.OrdinalIgnoreCase)
             || (r.Latest.FileName?.Contains(wagoLabel, StringComparison.OrdinalIgnoreCase) ?? false));

        var forFlavor = candidates.Where(r => r.Latest.FlavorMatched)
            .OrderByDescending(SameAsWago)
            .ThenByDescending(r => r.Latest.Date ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
        if (forFlavor != null) return forFlavor;

        var neutralSameVersion = candidates.FirstOrDefault(r =>
            SameAsWago(r) && AssetPicker.Score(r.Latest.FileName ?? r.Latest.Version, flavor, out _) >= 0);
        if (neutralSameVersion != null) neutralSameVersion.Latest.FlavorMatched = true;
        return neutralSameVersion;
    }

    public Task<Search.SearchOutcome> SearchAsync(Search.SearchQuery query, CancellationToken ct = default) =>
        Search.SearchAsync(query, Ctx, ct);


    public TrackedAddon? FindTracked(string provider, string projectId) =>
        Data.Addons.FirstOrDefault(a => a.Provider == provider && a.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));

    public async Task<TrackedAddon> InstallNewAsync(RemoteAddon remote, string sourceUrl, bool allowPrerelease, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var a = FindTracked(remote.Provider, remote.ProjectId);
            var isNew = a == null;
            a ??= new TrackedAddon
            {
                Name = remote.Name,
                Provider = remote.Provider,
                ProjectId = remote.ProjectId,
                SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? remote.WebsiteUrl : sourceUrl,
            };
            a.AllowPrerelease = allowPrerelease;
            a.Latest = remote.Latest;
            a.LastChecked = DateTimeOffset.Now;
            a.LastError = null;
            if (isNew) lock (_dbGate) Data.Addons.Add(a);
            try
            {
                await InstallCoreAsync(a, ct).ConfigureAwait(false);
            }
            catch
            {
                if (isNew && a.InstalledAt == null) lock (_dbGate) Data.Addons.Remove(a);
                throw;
            }
            return a;
        }
        finally { _opLock.Release(); Scan(); Report("Ready"); }
    }

    public async Task UpdateAsync(TrackedAddon a, CancellationToken ct = default)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (a.Latest == null || a.LastError != null)
            {
                await CheckOneCoreAsync(a, ct).ConfigureAwait(false);
                if (a.LastError != null) throw new ProviderException(a.LastError);
            }
            await InstallCoreAsync(a, ct).ConfigureAwait(false);
        }
        finally { _opLock.Release(); Scan(); Report("Ready"); }
    }

    public async Task<UpdateSummary> UpdateAllAsync(bool install, CancellationToken ct = default)
    {
        var summary = new UpdateSummary();
        if (Flavor == null) return summary;
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CheckAllCoreAsync(ct).ConfigureAwait(false);
            var todo = Entries.Where(e => e.Tracked != null && e.Status == UpdateStatus.UpdateAvailable && !e.Tracked.Pinned)
                              .Select(e => e.Tracked!).ToList();
            summary.Available.AddRange(todo.Select(t => t.Name));
            if (!install || todo.Count == 0) return summary;

            if (Settings.BackupWtfBeforeUpdate)
            {
                Report("Backing up addon settings (WTF)...");
                try { await Task.Run(() => BackupService.BackupWtf(Flavor, Settings.WtfBackupsToKeep, "before updates"), ct).ConfigureAwait(false); }
                catch (Exception ex) { Log.Error("WTF backup failed", ex); }
            }

            var i = 0;
            foreach (var a in todo)
            {
                i++;
                Report($"Updating {a.Name} ({i}/{todo.Count})...", (double)(i - 1) / todo.Count);
                try
                {
                    await InstallCoreAsync(a, ct).ConfigureAwait(false);
                    summary.Updated.Add($"{a.Name} {a.InstalledVersion}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    summary.Failed.Add((a.Name, ex.Message));
                    Log.Error($"Update of {a.Name} failed", ex);
                }
            }
            return summary;
        }
        finally { _opLock.Release(); Scan(); Report("Ready"); }
    }

    private async Task InstallCoreAsync(TrackedAddon a, CancellationToken ct)
    {
        var flavor = Flavor ?? throw new InvalidOperationException("No WoW folder selected.");
        var rel = a.Latest ?? throw new ProviderException("No release information yet - check for updates first.");
        if (string.IsNullOrWhiteSpace(rel.DownloadUrl)) throw new ProviderException("The source has no download for this addon.");

        var headers = new Dictionary<string, string>(rel.DownloadHeaders ?? new());
        var host = new Uri(rel.DownloadUrl).Host;
        if (host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && Settings.GitHubToken is { Length: > 0 } token)
            headers["Authorization"] = "Bearer " + token;

        var tmp = Path.Combine(AppPaths.TempDir, $"{Guid.NewGuid():N}.zip");
        try
        {
            Report($"Downloading {a.Name} {rel.Version}...", 0);
            var progress = new Progress<double>(p => Report($"Downloading {a.Name} {rel.Version}...", p));
            await Installer.DownloadAsync(rel.DownloadUrl, headers, tmp, progress, ct).ConfigureAwait(false);

            Report($"Installing {a.Name}...");
            await Task.Run(() =>
            {
                var newFolders = Installer.Inspect(tmp).Select(f => f.TargetName).ToList();
                if (newFolders.Count == 0) throw new InvalidDataException("No addon found in the download (it contains no .toc file).");
                BackupService.BackupAddon(flavor.Key, flavor.AddOnsPath, a.Name, a.InstalledVersion, a.InstalledReleaseId, a.Id,
                    a.Folders.Concat(newFolders), Settings.AddonBackupsToKeep);
                var installed = Installer.InstallZip(tmp, flavor.AddOnsPath, a.Folders);
                lock (_dbGate)
                {
                    TakeOverFolders(a, installed);
                    a.Folders = installed;
                    a.InstalledVersion = rel.Version;
                    a.InstalledReleaseId = rel.ReleaseId;
                    a.InstalledAt = DateTimeOffset.Now;
                    a.AutoDetected = false;
                    a.LastError = null;
                    foreach (var f in installed) Data.NoAutoLink.RemoveAll(n => n.Equals(f, StringComparison.OrdinalIgnoreCase));
                }
                SaveDb();
            }, ct).ConfigureAwait(false);
            Log.Info($"Installed {a.Name} {rel.Version} ({string.Join(", ", a.Folders)}) from {a.Provider}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private void TakeOverFolders(TrackedAddon owner, IEnumerable<string> folders)
    {
        var set = folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in Data.Addons.Where(o => o != owner).ToList())
        {
            if (other.Folders.RemoveAll(set.Contains) > 0 && other.Folders.Count == 0)
            {
                Data.Addons.Remove(other);
                Log.Info($"{other.Name} is now part of {owner.Name}");
            }
        }
    }

    public async Task<TrackedAddon> LinkAsync(AddonEntry entry, RemoteAddon remote, string sourceUrl, CancellationToken ct = default)
    {
        TrackedAddon a;
        lock (_dbGate)
        {
            var main = entry.Folders.FirstOrDefault();
            a = FindTracked(remote.Provider, remote.ProjectId) ?? new TrackedAddon
            {
                Name = remote.Name,
                Provider = remote.Provider,
                ProjectId = remote.ProjectId,
                SourceUrl = sourceUrl,
                InstalledVersion = main?.Version,
            };
            if (!Data.Addons.Contains(a)) Data.Addons.Add(a);
            foreach (var f in entry.FolderNames)
            {
                if (!a.Folders.Contains(f, StringComparer.OrdinalIgnoreCase)) a.Folders.Add(f);
                Data.NoAutoLink.RemoveAll(n => n.Equals(f, StringComparison.OrdinalIgnoreCase));
            }
            TakeOverFolders(a, a.Folders);
            a.Latest = remote.Latest;
            a.LastChecked = DateTimeOffset.Now;
            a.LastError = null;
            if (string.IsNullOrEmpty(a.InstalledReleaseId) && VersionComparer.Compare(a.InstalledVersion, remote.Latest.Version) == 0)
                a.InstalledReleaseId = remote.Latest.ReleaseId;
        }
        SaveDb();
        Scan();
        await Task.CompletedTask;
        return a;
    }

    public void Unlink(TrackedAddon a)
    {
        lock (_dbGate)
        {
            Data.Addons.Remove(a);
            foreach (var f in a.Folders)
                if (!Data.NoAutoLink.Contains(f, StringComparer.OrdinalIgnoreCase)) Data.NoAutoLink.Add(f);
        }
        SaveDb();
        Scan();
    }

    public async Task UninstallAsync(AddonEntry entry)
    {
        var flavor = Flavor!;
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var folders = entry.FolderNames.ToList();
            await Task.Run(() =>
            {
                BackupService.BackupAddon(flavor.Key, flavor.AddOnsPath, entry.Name, entry.InstalledVersion, entry.Tracked?.InstalledReleaseId,
                    entry.Tracked?.Id, folders, Settings.AddonBackupsToKeep);
                Installer.RemoveFolders(flavor.AddOnsPath, folders);
            }).ConfigureAwait(false);
            if (entry.Tracked != null) lock (_dbGate) Data.Addons.Remove(entry.Tracked);
            SaveDb();
            Log.Info($"Uninstalled {entry.Name} ({string.Join(", ", folders)}) - a backup was kept");
        }
        finally { _opLock.Release(); Scan(); }
    }

    public void SetPinned(TrackedAddon a, bool pinned) { a.Pinned = pinned; SaveDb(); Scan(); }

    public void SetAllowPrerelease(TrackedAddon a, bool allow)
    {
        a.AllowPrerelease = allow;
        a.Latest = null;
        SaveDb();
        Scan();
    }

    public async Task RestoreBackupAsync(AddonBackupInfo backup)
    {
        var flavor = Flavor!;
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            TrackedAddon? a;
            lock (_dbGate)
            {
                a = Data.Addons.FirstOrDefault(x => x.Id == backup.TrackedId)
                    ?? Data.Addons.FirstOrDefault(x => x.Folders.Intersect(backup.Folders, StringComparer.OrdinalIgnoreCase).Any());
            }
            var current = a?.Folders ?? backup.Folders;
            await Task.Run(() =>
            {
                BackupService.BackupAddon(flavor.Key, flavor.AddOnsPath, a?.Name ?? backup.AddonName, a?.InstalledVersion, a?.InstalledReleaseId, a?.Id,
                    current, Settings.AddonBackupsToKeep + 1);
                var installed = Installer.InstallZip(backup.Path, flavor.AddOnsPath, current);
                if (a != null)
                {
                    lock (_dbGate)
                    {
                        a.Folders = installed;
                        a.InstalledVersion = backup.Version;
                        a.InstalledReleaseId = backup.ReleaseId;
                        a.InstalledAt = DateTimeOffset.Now;
                        a.Pinned = true;
                    }
                }
            }).ConfigureAwait(false);
            SaveDb();
            Log.Info($"Restored {backup.AddonName} {backup.Version} from backup{(a != null ? " (pinned)" : "")}");
        }
        finally { _opLock.Release(); Scan(); }
    }

    public List<ShareEntry> ExportList() =>
        Data.Addons.Select(a => new ShareEntry { Name = a.Name, Provider = a.Provider, ProjectId = a.ProjectId, Url = a.SourceUrl, Prerelease = a.AllowPrerelease })
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<UpdateSummary> ImportAsync(IReadOnlyList<ShareEntry> entries, CancellationToken ct = default)
    {
        var summary = new UpdateSummary();
        var i = 0;
        foreach (var e in entries)
        {
            i++;
            Report($"Importing {e.Name} ({i}/{entries.Count})...", (double)(i - 1) / entries.Count);
            try
            {
                string provider = e.Provider, id = e.ProjectId;
                if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(id))
                {
                    var parsed = Providers.Parse(e.Url ?? "");
                    provider = parsed.Provider.Key;
                    id = parsed.ProjectId;
                }
                var p = Providers.Get(provider) ?? throw new ProviderException($"Unknown source \"{provider}\".");
                var existing = FindTracked(provider, id);
                if (existing != null && existing.Folders.Any(f => Directory.Exists(Path.Combine(Flavor!.AddOnsPath, f)))) continue;

                var remote = await p.GetLatestAsync(id, e.Prerelease, Ctx, ct).ConfigureAwait(false);
                await InstallNewAsync(remote, e.Url ?? remote.WebsiteUrl, e.Prerelease, ct).ConfigureAwait(false);
                summary.Updated.Add(remote.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                summary.Failed.Add((string.IsNullOrEmpty(e.Name) ? e.Url ?? "?" : e.Name, ex.Message));
                Log.Error($"Import of {e.Name} failed", ex);
            }
        }
        Report("Ready");
        return summary;
    }

    public int ImportFromWowUp()
    {
        var flavor = Flavor!;
        var linked = 0;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var dir in new[] { "WowUp", "WowUpCf" }.Select(d => Path.Combine(appData, d)))
        {
            var file = Path.Combine(dir, "addons.json");
            if (!File.Exists(file)) continue;
            var installs = ReadWowUpInstallations(Path.Combine(dir, "preferences.json"));
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var e = prop.Value;
                    var installId = e.Str("installationId");
                    if (installId != null && installs.TryGetValue(installId, out var loc) &&
                        !loc.Equals(flavor.Path, StringComparison.OrdinalIgnoreCase)) continue;

                    var folders = (e.Str("installedFolders") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (folders.Count == 0 || !folders.All(f => Directory.Exists(Path.Combine(flavor.AddOnsPath, f)))) continue;

                    var candidates = new List<(string Provider, string Id)>();
                    void Add(string? name, string? id)
                    {
                        var key = MapWowUpProvider(name);
                        if (key != null && !string.IsNullOrWhiteSpace(id)) candidates.Add((key, id!));
                    }
                    Add(e.Str("providerName"), e.Str("externalId"));
                    foreach (var x in e.Arr("externalIds")) Add(x.Str("providerName"), x.Str("id"));
                    var hasWago = !string.IsNullOrWhiteSpace(Settings.WagoApiKey);
                    var pick = candidates.FirstOrDefault(c => c.Provider != "wago" || hasWago);
                    if (pick.Provider == null) continue;

                    lock (_dbGate)
                    {
                        if (Data.Addons.Any(a => a.Folders.Intersect(folders, StringComparer.OrdinalIgnoreCase).Any() && !a.AutoDetected)) continue;
                        Data.Addons.RemoveAll(a => a.AutoDetected && a.Folders.Intersect(folders, StringComparer.OrdinalIgnoreCase).Any());
                        var p = Providers.Get(pick.Provider)!;
                        var keepReleaseId = pick.Provider is "wowup" or "curseforge" && pick.Provider == MapWowUpProvider(e.Str("providerName"));
                        Data.Addons.Add(new TrackedAddon
                        {
                            Name = e.Str("name") ?? folders[0],
                            Provider = pick.Provider,
                            ProjectId = pick.Id,
                            SourceUrl = pick.Provider == MapWowUpProvider(e.Str("providerName")) ? e.Str("externalUrl") ?? p.WebsiteFor(pick.Id) : p.WebsiteFor(pick.Id),
                            Folders = folders,
                            InstalledVersion = e.Str("installedVersion"),
                            InstalledReleaseId = keepReleaseId ? e.Str("installedExternalReleaseId") : null,
                            AllowPrerelease = (e.Long("channelType") ?? 0) != 0,
                            Pinned = e.Bool("isIgnored"),
                            AutoDetected = true,
                        });
                        foreach (var f in folders) Data.NoAutoLink.RemoveAll(n => n.Equals(f, StringComparison.OrdinalIgnoreCase));
                        linked++;
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Reading WowUp data from {dir} failed", ex); }
        }
        if (linked > 0) { SaveDb(); Log.Info($"Imported {linked} addon source(s) from WowUp"); }
        Scan();
        return linked;
    }

    private static string? MapWowUpProvider(string? name) => name?.ToLowerInvariant() switch
    {
        "github" => "github",
        "curse" or "curseforge" => "curseforge",
        "wowinterface" => "wowinterface",
        "wago" => "wago",
        "tukui" => "tukui",
        "wowuphub" => "wowup",
        _ => null,
    };

    private static Dictionary<string, string> ReadWowUpInstallations(string prefsFile)
    {
        var map = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(prefsFile)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(prefsFile));
            var inst = doc.RootElement.Prop("wow_installations");
            JsonDocument? inner = null;
            if (inst.ValueKind == JsonValueKind.String) { inner = JsonDocument.Parse(inst.GetString()!); inst = inner.RootElement; }
            using (inner)
            {
                foreach (var i in inst.Arr())
                {
                    var id = i.Str("id");
                    var loc = i.Str("location");
                    if (id != null && loc != null) map[id] = Path.GetDirectoryName(loc)!.TrimEnd('\\');
                }
            }
        }
        catch { }
        return map;
    }
}
