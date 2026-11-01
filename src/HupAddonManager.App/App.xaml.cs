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
    private static readonly string InstanceKey = InstanceSuffix();
    private static readonly string MutexName = @"Local\HupAddonManager.SingleInstance" + InstanceKey;
    private static readonly string ShowEventName = @"Local\HupAddonManager.Show" + InstanceKey;
    private static readonly string ExitEventName = @"Local\HupAddonManager.Exit" + InstanceKey;

    private static string InstanceSuffix()
    {
        var data = Environment.GetEnvironmentVariable("HUPADDON_DATA");
        if (string.IsNullOrWhiteSpace(data)) return "";
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(data.Trim().ToLowerInvariant()));
        return "." + Convert.ToHexString(hash)[..8];
    }

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;
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
        if (!isFirst && !background) isFirst = TryReplaceOtherCopy();
        if (!isFirst)
        {
            if (!background && EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                if (_exiting) return;
                if (which == 0) Dispatcher.BeginInvoke(ShowMainWindow);
                else { Dispatcher.BeginInvoke(ExitApp); return; }
            }
        }) { IsBackground = true, Name = "instance-signal" }.Start();

        DispatcherUnhandledException += OnUnhandled;
        Log.Prune();
        SelfUpdater.CleanupOld();
        Log.Info($"{AppInfo.Name} {AppInfo.VersionText} starting{(background ? " in background mode" : "")}");

        Manager = new AddonManager();
        if (!AppPaths.IsTestInstance) Autostart.Sync(Manager.Settings.StartWithWindows);
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
            ViewModel.SelectedTab = 3;
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

    private bool TryReplaceOtherCopy()
    {
        var self = Environment.ProcessPath;
        var current = System.Diagnostics.Process.GetCurrentProcess();
        System.Diagnostics.Process? other = null;
        string? otherPath = null;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id == current.Id) continue;
            try { otherPath = p.MainModule?.FileName; other = p; break; } catch { }
        }
        if (other == null || otherPath == null || self == null || otherPath.Equals(self, StringComparison.OrdinalIgnoreCase)) return false;

        var otherVersion = other.MainModule?.FileVersionInfo.ProductVersion?.Split('+')[0] ?? "?";
        var choice = Dialogs.Show("Already running",
            $"Another copy of {AppInfo.Name} is already running:\n{otherPath} (v{otherVersion})\n\n" +
            $"Close it and start this copy instead?\n{self} (v{AppInfo.VersionText})",
            new[] { "Start this copy", "Keep the running one" }, "Start this copy");
        if (choice != "Start this copy") return false;

        if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
        {
            exitEvent.Set();
            exitEvent.Dispose();
        }
        else
        {
            try { other.Kill(); } catch { }
        }
        try { return _mutex!.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { return true; }
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
