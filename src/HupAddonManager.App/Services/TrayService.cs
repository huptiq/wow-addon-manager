using System.Windows;
using HupAddonManager.Core.Storage;
using WinForms = System.Windows.Forms;

namespace HupAddonManager.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _updateAllItem;

    public event Action? OpenRequested;
    public event Action? CheckRequested;
    public event Action? UpdateAllRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
        _icon = new WinForms.NotifyIcon
        {
            Icon = iconStream != null ? new System.Drawing.Icon(iconStream) : System.Drawing.SystemIcons.Application,
            Text = AppInfo.Name,
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("Check for updates", null, (_, _) => CheckRequested?.Invoke());
        _updateAllItem = new WinForms.ToolStripMenuItem("Update all", null, (_, _) => UpdateAllRequested?.Invoke());
        menu.Items.Add(_updateAllItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    public void SetUpdateCount(int count)
    {
        var text = count > 0 ? $"{AppInfo.Name} - {count} update{(count == 1 ? "" : "s")}" : AppInfo.Name;
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _updateAllItem.Text = count > 0 ? $"Update all ({count})" : "Update all";
    }

    public void Notify(string title, string text, bool warning = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 250) text = text[..247] + "...";
        _icon.ShowBalloonTip(6000, title, text, warning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
