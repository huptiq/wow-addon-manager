using System.Windows;
using System.Windows.Input;
using HupAddonManager.Core;
using HupAddonManager.Core.Models;
using HupAddonManager.Core.Providers;

namespace HupAddonManager.App.Views;

public partial class AddAddonWindow : Window
{
    private readonly AddonManager _manager;
    private readonly AddonEntry? _linkTarget;
    private RemoteAddon? _remote;
    private string _lookedUpLink = "";
    private CancellationTokenSource? _cts;

    public TrackedAddon? Result { get; private set; }

    public AddAddonWindow(AddonManager manager, AddonEntry? linkTarget = null, string? initialLink = null, bool autoLookup = false)
    {
        InitializeComponent();
        Services.DarkTitleBar.Attach(this);
        _manager = manager;
        _linkTarget = linkTarget;
        if (linkTarget != null)
        {
            Title = "Link AddOn";
            HeaderText.Text = $"Link '{linkTarget.Name}' to its download page";
            HintText.Text = "Paste the link where this addon is published (GitHub, CurseForge, Wago Addons, WoWInterface, ...). " +
                            "The installed files are kept; future updates come from that source.";
            InstallButton.Content = "Link";
            BetaBox.IsChecked = linkTarget.Tracked?.AllowPrerelease == true;
        }
        if (!string.IsNullOrWhiteSpace(initialLink)) LinkBox.Text = initialLink;
        else if (Clipboard.ContainsText() && LooksLikeLink(Clipboard.GetText())) LinkBox.Text = Clipboard.GetText().Trim();
        Loaded += async (_, _) =>
        {
            LinkBox.Focus();
            LinkBox.SelectAll();
            if (autoLookup && LinkBox.Text.Length > 0) await LookupAsync();
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    private static bool LooksLikeLink(string s)
    {
        s = s.Trim();
        return s.Length < 400 && !s.Contains('\n') && (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.Contains(".com/") || s.Contains(".io/"));
    }

    private async void LinkBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_remote != null && LinkBox.Text.Trim() == _lookedUpLink) Install_Click(sender, e);
        else await LookupAsync();
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e) => await LookupAsync();

    private async void BetaBox_Click(object sender, RoutedEventArgs e)
    {
        if (_remote != null) await LookupAsync();
    }

    private void SetBusy(bool busy, string? text = null, bool error = false)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LookupButton.IsEnabled = !busy;
        LinkBox.IsEnabled = !busy;
        BetaBox.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && _remote != null;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = text ?? "";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Red" : "Muted");
    }

    private async Task LookupAsync()
    {
        var link = LinkBox.Text.Trim();
        _remote = null;
        ResultCard.Visibility = Visibility.Collapsed;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true, "Looking up...");
        try
        {
            var remote = await _manager.LookupAsync(link, BetaBox.IsChecked == true, _cts.Token);
            _remote = remote;
            _lookedUpLink = link;
            ShowResult(remote);
            var existing = _manager.FindTracked(remote.Provider, remote.ProjectId);
            var note = existing != null && _linkTarget == null ? "Already installed - installing again will reinstall the latest version." : null;
            if (remote.Provider != "wago" && WagoWeb.IsWagoLink(link))
            {
                var source = _manager.Providers.Get(remote.Provider)?.DisplayName ?? remote.Provider;
                var wagoNote = $"Found on Wago Addons. Wago only lets apps download its files with a paid Patreon key, " +
                               $"so this installs the same addon from {source} (linked on its Wago page). Updates will also come from {source}.";
                note = note == null ? wagoNote : wagoNote + "\n\n" + note;
            }
            SetBusy(false, note);
            InstallButton.Focus();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetBusy(false, ex is ProviderException ? ex.Message : $"Couldn't look up this link: {ex.Message}", error: true);
        }
    }

    private void ShowResult(RemoteAddon r)
    {
        var flavor = _manager.Flavor!;
        var provider = _manager.Providers.Get(r.Provider)?.DisplayName ?? r.Provider;
        NameText.Text = r.Name;
        if (r.Provider != "wago" && WagoWeb.IsWagoLink(LinkBox.Text)) provider += " (found on Wago Addons)";
        ByText.Text = string.IsNullOrWhiteSpace(r.Author) ? $"from {provider}" : $"by {r.Author} · from {provider}";
        SummaryText.Text = r.Summary ?? "";
        SummaryText.Visibility = string.IsNullOrWhiteSpace(r.Summary) ? Visibility.Collapsed : Visibility.Visible;
        var rel = r.Latest;
        VersionText.Text = rel.Version + (rel.Date is { } d ? $"  ·  {d.LocalDateTime:d}" : "") + (rel.IsPrerelease ? "  ·  beta/alpha" : "");
        FileText.Text = (rel.FileName ?? "-") + (rel.Size is { } s ? $"  ({s / 1024.0 / 1024.0:0.0} MB)" : "");
        if (rel.FlavorMatched)
        {
            FlavorText.Text = $"✔ Made for {flavor.DisplayName}";
            FlavorText.Foreground = (System.Windows.Media.Brush)FindResource("Green");
        }
        else
        {
            FlavorText.Text = $"⚠ No {flavor.DisplayName}-specific download found - the generic file is used. It may or may not work in {flavor.DisplayName}.";
            FlavorText.Foreground = (System.Windows.Media.Brush)FindResource("Orange");
        }
        ChangelogText.Text = string.IsNullOrWhiteSpace(rel.Changelog) ? "(no changelog provided)" : rel.Changelog;
        ChangelogExpander.IsExpanded = false;
        ResultCard.Visibility = Visibility.Visible;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_remote == null) return;
        if (_manager.IsBusy)
        {
            SetBusy(false, "Another operation is still running. Try again in a moment.", error: true);
            return;
        }
        void OnProgress(string text, double? _) => Dispatcher.BeginInvoke(() => StatusText.Text = text);
        _manager.Progress += OnProgress;
        SetBusy(true, _linkTarget != null ? "Linking..." : "Installing...");
        InstallButton.IsEnabled = false;
        try
        {
            Result = _linkTarget != null
                ? await _manager.LinkAsync(_linkTarget, _remote, _lookedUpLink)
                : await _manager.InstallNewAsync(_remote, _lookedUpLink, BetaBox.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            SetBusy(false, (_linkTarget != null ? "Linking failed: " : "Install failed: ") + ex.Message, error: true);
        }
        finally
        {
            _manager.Progress -= OnProgress;
        }
    }
}
