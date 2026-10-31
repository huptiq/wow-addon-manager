using System.Windows;
using HupAddonManager.App.Views;

namespace HupAddonManager.App.Services;

public static class Dialogs
{
    private static Window? Owner
    {
        get
        {
            var w = Application.Current?.MainWindow;
            return w is { IsVisible: true } ? w : null;
        }
    }

    public static string? Show(string title, string message, string[] buttons, string? primary = null)
    {
        var w = new MessageWindow(title, message, buttons, primary);
        if (Owner != null) w.Owner = Owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w.Result : null;
    }

    public static void Info(string title, string message) => Show(title, message, new[] { "OK" }, "OK");

    public static void Error(string title, string message) => Show(title, message, new[] { "OK" }, "OK");

    public static bool Confirm(string title, string message, string yes = "Yes", string no = "Cancel") =>
        Show(title, message, new[] { yes, no }, yes) == yes;

    public static T ShowDialog<T>(T window) where T : Window
    {
        if (Owner != null) window.Owner = Owner; else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return window;
    }
}
