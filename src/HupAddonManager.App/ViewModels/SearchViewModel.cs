using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HupAddonManager.App.Services;
using HupAddonManager.App.Views;
using HupAddonManager.Core.Search;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.SystemIntegration;

namespace HupAddonManager.App.ViewModels;

public sealed class SearchRow
{
    public SearchRow(SearchResult r, bool installed, string flavorName)
    {
        Result = r;
        Installed = installed;
        FlavorBadge = "✔ " + flavorName.Replace("WoW: ", "");
        FlavorTip = r.ForFlavor == true ? $"Made for {flavorName}" : $"Not marked for {flavorName}. It may or may not work.";
    }

    public SearchResult Result { get; }
    public bool Installed { get; }
    public string Name => Result.Name;
    private string SourceText => Result.Provider == "wago" ? "Wago Addons (installs from its GitHub/CurseForge source)" : Result.SourceName;
    public string Byline => string.IsNullOrWhiteSpace(Result.Author) ? SourceText : $"by {Result.Author} · {SourceText}";
    public string? Summary => Result.Summary;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Result.Summary);
    public string CategoryText => string.Join(", ", Result.Categories.Take(3));
    public string DownloadsText => Result.Provider == "github" ? "" : FormatCount(Result.Downloads);
    public string UpdatedText => Result.Updated is { } d ? "Updated " + d.LocalDateTime.ToString("d") : "";
    public bool IsForFlavor => Result.ForFlavor == true;
    public string FlavorTip { get; }
    public string FlavorBadge { get; }
    public string InstallText => Installed ? "Reinstall" : "Install";

    private static string FormatCount(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M downloads",
        >= 1_000 => $"{n / 1_000.0:0.#}k downloads",
        > 0 => $"{n} downloads",
        _ => "",
    };
}

public sealed partial class SearchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private bool _loaded;

    public ObservableCollection<SearchRow> Results { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _onlyForFlavor;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _sourcesText = "";

    public SearchViewModel(MainViewModel main) => _main = main;

    public string OnlyForFlavorLabel => $"Only show addons made for {_main.Manager.Flavor?.DisplayName ?? "this client"}";
    public bool IsEmpty => !IsSearching && Results.Count == 0;

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnOnlyForFlavorChanged(bool value) { if (_loaded) _ = SearchAsync(); }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded || _main.Manager.Flavor == null) return;
        _loaded = true;
        OnPropertyChanged(nameof(OnlyForFlavorLabel));
        await SearchAsync();
    }

    public void FlavorChanged()
    {
        _loaded = false;
        Results.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (_main.Manager.Flavor == null) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsSearching = true;
        StatusText = string.IsNullOrWhiteSpace(Query) ? "Loading popular addons..." : "Searching...";
        try
        {
            var outcome = await _main.Manager.SearchAsync(new SearchQuery
            {
                Text = Query.Trim(),
                OnlyForFlavor = OnlyForFlavor,
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            var flavorName = _main.Manager.Flavor.DisplayName;
            Results.Clear();
            foreach (var r in outcome.Results.Take(150))
            {
                var installed = IsInstalled(r);
                Results.Add(new SearchRow(r, installed, flavorName));
            }
            StatusText = Results.Count == 0 ? "Nothing found. Try another word." :
                string.IsNullOrWhiteSpace(Query) ? $"Popular addons ({Results.Count})" : $"{Results.Count} results";
            SourcesText = string.Join("   ", outcome.Sources.Select(s => s.Note == null ? $"{s.SourceName}: {s.Count}" : $"{s.SourceName}: {s.Note}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText = "Search failed: " + ex.Message;
            Log.Error("Search failed", ex);
        }
        finally
        {
            if (_cts == cts) IsSearching = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private bool IsInstalled(SearchResult r)
    {
        if (_main.Manager.FindTracked(r.Provider, r.ProjectId) != null) return true;
        var name = r.Name.Trim();
        return _main.Manager.Entries.Any(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || e.Name.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
            || e.FolderNames.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    [RelayCommand]
    private async Task InstallAsync(SearchRow? row)
    {
        if (row == null || _main.Manager.Flavor == null) return;
        if (_main.Manager.IsBusy)
        {
            Dialogs.Info("Please wait", "Another operation is still running. Try again when it's finished.");
            return;
        }
        var w = Dialogs.ShowDialog(new AddAddonWindow(_main.Manager, null, row.Result.Link, autoLookup: true));
        if (w.Result == null) return;
        var index = Results.IndexOf(row);
        if (index >= 0) Results[index] = new SearchRow(row.Result, true, _main.Manager.Flavor.DisplayName);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenPage(SearchRow? row)
    {
        if (row != null) Shell.Open(row.Result.Link);
    }
}
