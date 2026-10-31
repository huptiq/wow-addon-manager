using System.Windows.Media;
using HupAddonManager.Core;
using HupAddonManager.Core.Models;

namespace HupAddonManager.App.ViewModels;

public enum RowAction { None, Update, Link, Reinstall, Retry }

public sealed class AddonRowViewModel
{
    public AddonRowViewModel(AddonEntry entry, string sourceName, string flavorName, int? expectedInterface)
    {
        Entry = entry;
        SourceName = sourceName;
        var t = entry.Tracked;

        Name = entry.Name;
        FoldersText = string.Join(", ", entry.FolderNames);
        InstalledVersion = string.IsNullOrWhiteSpace(entry.InstalledVersion) ? "-" : CleanVersion(entry.InstalledVersion!, entry);
        LatestVersion = entry.LatestVersion is { } lv ? CleanVersion(lv, entry) : (t == null ? "" : "-");
        IsPinned = t?.Pinned == true;
        IsBeta = t?.AllowPrerelease == true;
        ShowIncompatible = entry.Compatible == false;
        IncompatibleTip = $"This addon doesn't say it supports {flavorName}" +
                          (expectedInterface != null ? $" (interface {expectedInterface})" : "") +
                          ". It may not load, or may cause Lua errors. Check \"Load out of date AddOns\" in game if you want to try it.";

        (StatusText, StatusColor, Action) = entry.Status switch
        {
            UpdateStatus.UpToDate => ("Up to date", "Green", RowAction.None),
            UpdateStatus.UpdateAvailable when IsPinned => ("Pinned · update available", "Muted", RowAction.None),
            UpdateStatus.UpdateAvailable => ("Update available", "Orange", RowAction.Update),
            UpdateStatus.NotChecked => ("Not checked yet", "Muted", RowAction.None),
            UpdateStatus.Unknown => ("Version unknown", "Blue", RowAction.Reinstall),
            UpdateStatus.Error => ("Error", "Red", RowAction.Retry),
            UpdateStatus.Unlinked => ("No source linked", "Muted", RowAction.Link),
            UpdateStatus.Missing => ("Folder missing", "Red", RowAction.Reinstall),
            _ => ("", "Muted", RowAction.None),
        };

        StatusTip = entry.Status switch
        {
            UpdateStatus.Error => t?.LastError ?? "Unknown error",
            UpdateStatus.Unknown => "The installed version can't be compared with the latest one. Reinstall once so the manager knows exactly what is installed.",
            UpdateStatus.Unlinked => "The manager doesn't know where this addon comes from. Click \"Link...\" and paste its download page.",
            UpdateStatus.Missing => "The addon's folder was deleted from AddOns. Reinstall it or unlink it.",
            UpdateStatus.UpdateAvailable when IsPinned => "This addon is pinned and won't be updated. Unpin it from the ⋯ menu.",
            _ => t?.LastChecked is { } c ? $"Last checked {c.LocalDateTime:g}" : null,
        };

        ActionText = Action switch
        {
            RowAction.Update => "Update",
            RowAction.Link => "Link...",
            RowAction.Reinstall => "Reinstall",
            RowAction.Retry => "Retry",
            _ => "",
        };
        Subtitle = entry.Notes;
    }

    private static string CleanVersion(string version, AddonEntry entry)
    {
        var v = version.Trim();
        if (v.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) v = v[..^4];
        foreach (var prefix in new[] { entry.Name }.Concat(entry.FolderNames))
        {
            if (prefix.Length > 0 && v.Length > prefix.Length + 1 && v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && v[prefix.Length] is ' ' or '-' or '_')
            {
                v = v[(prefix.Length + 1)..].Trim();
                break;
            }
        }
        return v;
    }

    public AddonEntry Entry { get; }
    public TrackedAddon? Tracked => Entry.Tracked;
    public bool IsTracked => Entry.Tracked != null;
    public string Key => Entry.Key;

    public string Name { get; }
    public string FoldersText { get; }
    public string? Subtitle { get; }
    public string InstalledVersion { get; }
    public string LatestVersion { get; }
    public string SourceName { get; }
    public string StatusText { get; }
    public string StatusColor { get; }
    public string? StatusTip { get; }
    public RowAction Action { get; }
    public string ActionText { get; }
    public bool HasAction => Action != RowAction.None;
    public bool IsPrimaryAction => Action == RowAction.Update;
    public bool IsPinned { get; }
    public bool IsBeta { get; }
    public bool ShowIncompatible { get; }
    public string IncompatibleTip { get; }

    public Brush StatusBrush => (Brush)System.Windows.Application.Current.Resources[StatusColor];
    public Brush StatusBackground
    {
        get
        {
            var c = ((SolidColorBrush)StatusBrush).Color;
            var b = new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }

    public bool Matches(string filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || FoldersText.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || SourceName.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || StatusText.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
