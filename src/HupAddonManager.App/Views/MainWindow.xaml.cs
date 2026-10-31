using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using HupAddonManager.App.ViewModels;

namespace HupAddonManager.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        Services.DarkTitleBar.Attach(this);
        Loaded += (_, _) =>
        {
            var s = VM.Manager.Settings;
            CurseKeyBox.Password = s.CurseForgeApiKey ?? "";
            WagoKeyBox.Password = s.WagoApiKey ?? "";
            GitHubKeyBox.Password = s.GitHubToken ?? "";
            ((INotifyCollectionChanged)VM.LogLines).CollectionChanged += (_, _) =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            };
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _ = VM.CheckAllAsync(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { VM.SelectedTab = 0; SearchBox.Focus(); e.Handled = true; }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { VM.AddAddonCommand.Execute(null); e.Handled = true; }
        };
    }

    private void SaveKeys_Click(object sender, RoutedEventArgs e) =>
        VM.Settings.SaveKeys(CurseKeyBox.Password, WagoKeyBox.Password, GitHubKeyBox.Password);

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ShareButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Item("Share my addon list...", VM.ExportListCommand));
        menu.Items.Add(Item("Import an addon list...", VM.ImportListCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Take over sources from WowUp", VM.ImportWowUpCommand));
        menu.Items.Add(Item("Open AddOns folder", VM.OpenAddOnsFolderCommand));
        menu.IsOpen = true;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AddonRowViewModel row } button) return;
        VM.SelectedRow = row;
        var menu = BuildRowMenu(row);
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddonList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as AddonRowViewModel ?? VM.SelectedRow;
        if (row == null) { e.Handled = true; return; }
        VM.SelectedRow = row;
        var menu = BuildRowMenu(row);
        AddonList.ContextMenu = menu;
    }

    private void AddonList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not AddonRowViewModel row) return;
        if (row.IsTracked) VM.ShowChangelogCommand.Execute(row);
        else VM.LinkRowCommand.Execute(row);
    }

    private ContextMenu BuildRowMenu(AddonRowViewModel row)
    {
        var m = new ContextMenu();
        if (row.IsTracked)
        {
            if (row.Action == RowAction.Update) m.Items.Add(Item("Update", VM.UpdateRowCommand, row));
            m.Items.Add(Item("Reinstall latest version", VM.UpdateRowCommand, row));
            m.Items.Add(Item("Check for updates", VM.CheckRowCommand, row));
            m.Items.Add(Item("Show changelog", VM.ShowChangelogCommand, row));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Pin this version (no updates)", VM.TogglePinCommand, row, isChecked: row.IsPinned));
            m.Items.Add(Item("Get beta / alpha versions", VM.ToggleBetaCommand, row, isChecked: row.IsBeta));
            m.Items.Add(Item("Change source...", VM.LinkRowCommand, row));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Open website", VM.OpenWebsiteCommand, row));
            m.Items.Add(Item("Open folder", VM.OpenFolderCommand, row));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Unlink (keep files)", VM.UnlinkCommand, row));
            m.Items.Add(Item("Uninstall...", VM.UninstallCommand, row));
        }
        else
        {
            m.Items.Add(Item("Link to a download page...", VM.LinkRowCommand, row));
            m.Items.Add(Item("Open folder", VM.OpenFolderCommand, row));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Uninstall...", VM.UninstallCommand, row));
        }
        return m;
    }

    private static MenuItem Item(string header, ICommand command, object? parameter = null, bool? isChecked = null)
    {
        var item = new MenuItem { Header = header, Command = command, CommandParameter = parameter };
        if (isChecked != null) { item.IsCheckable = false; item.IsChecked = isChecked.Value; }
        return item;
    }
}
