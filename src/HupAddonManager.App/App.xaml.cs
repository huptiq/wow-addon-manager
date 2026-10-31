using System.Windows;
using System.Windows.Threading;
using HupAddonManager.App.Services;
using HupAddonManager.App.ViewModels;
using HupAddonManager.App.Views;
using HupAddonManager.Core;
using HupAddonManager.Core.Storage;
using HupAddonManager.Core.SystemIntegration;

namespace HupAddonManager.App;

public partial class App : Application
{
    private const string MutexName = @"Local\HupAddonManager.SingleInstance";
    private const string ShowEventName = @"Local\HupAddonManager.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private bool _exiting;
    private bool _trayHintShown;

    public static new App? Current => Application.Current as App;
    public TrayService? Tray { get; private set; }
    public AutoUpdater? AutoUpdater { get; private set; }
    public MainViewModel? ViewModel { get; private set; }
    public AddonManager? Manager { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var background = e.Args.Any(a => a.Equals(Autostart.BackgroundArg, StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            if (!background && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne())
            {
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        }) { IsBackground = true, Name = "show-signal" }.Start();

        DispatcherUnhandledException += OnUnhandled;
        Log.Prune();
        SelfUpdater.CleanupOld();
        Log.Info($"{AppInfo.Name} {AppInfo.VersionText} starting{(background ? " in background mode" : "")}");

        Manager = new AddonManager();
        Autostart.Sync(Manager.Settings.StartWithWindows);
        var hasWow = false;
        try { hasWow = Manager.InitializeWow(); }
        catch (Exception ex) { Log.Error("Finding the WoW folder failed", ex); }

        Tray = new TrayService();
        ViewModel = new MainViewModel(Manager);
        AutoUpdater = new AutoUpdater(Manager, Tray);

        Tray.OpenRequested += ShowMainWindow;
        Tray.CheckRequested += () => _ = ViewModel.CheckAllAsync();
        Tray.UpdateAllRequested += () => ViewModel.UpdateAllCommand.Execute(null);
        Tray.ExitRequested += ExitApp;

        var window = new MainWindow { DataContext = ViewModel };
        MainWindow = window;
        window.Closing += (_, args) =>
        {
            if (_exiting || !Manager.Settings.CloseToTray) { ExitApp(); return; }
            args.Cancel = true;
            window.Hide();
            if (!_trayHintShown && Manager.Settings.ShowNotifications)
            {
                Tray.Notify(AppInfo.Name, "Still running in the tray and keeping your addons updated. Right-click the icon to exit.");
                _trayHintShown = true;
            }
        };

        if (hasWow)
        {
            Manager.Scan();
            ViewModel.Backups.Reload();
        }
        else
        {
            ViewModel.SelectedTab = 2;
            Log.Warn("No WoW folder found");
        }

        if (!background) window.Show();

        _ = CheckAppUpdateAsync();

        if (hasWow)
        {
            await AutoUpdater.RunAsync(background ? "boot" : "start-up", waitForNetwork: background);
            if (background && Manager.Settings.ExitAfterBootUpdate && !window.IsVisible)
            {
                Log.Info("Boot update done; exiting as configured");
                await Task.Delay(7000);
                if (!window.IsVisible) ExitApp();
            }
        }
        else if (background)
        {
            Tray.Notify(AppInfo.Name, "World of Warcraft folder not found. Open the manager to set it.", warning: true);
        }
    }

    private async Task CheckAppUpdateAsync()
    {
        var s = Manager!.Settings;
        if (!s.CheckForAppUpdates) return;
        try
        {
            var info = await SelfUpdater.CheckAsync();
            if (info == null) return;
            ViewModel!.AppUpdate = info;
            Log.Info($"App update available: {info.Tag}");
            if (s.ShowNotifications) Tray?.Notify("App update available", $"{AppInfo.Name} {info.Tag} is ready. Open the manager to install it.");
        }
        catch (Exception ex) { Log.Warn("App update check failed: " + ex.Message); }
    }

    public void ShowMainWindow()
    {
        var w = MainWindow;
        if (w == null) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
        w.Focus();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Exiting");
        Tray?.Dispose();
        _showEvent?.Set();
        Shutdown();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error", e.Exception);
        e.Handled = true;
        if (MainWindow?.IsVisible == true)
            Dialogs.Error("Something went wrong", e.Exception.Message + "\n\nDetails are in the log (Settings -> Open log).");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
