using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DeskBar.Bar;
using DeskBar.Core;
using DeskBar.Widgets;
using ManagedShell;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;

namespace DeskBar;

public partial class App : Application
{
    private const string ExitEventName = @"Local\DeskBar.Exit";
    private const string LegacyExitEventName = @"Local\LinkbarDesktops.Exit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private BarManager? _bars;
    private bool _cleanedUp;

    public static new App Current => (App)Application.Current;

    public ShellManager Shell { get; private set; } = null!;
    public VirtualDesktopService Desktops { get; private set; } = null!;
    public PinStore Pins { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (HasArg(args, "--exit"))
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var running))
                using (running) running.Set();
            Shutdown();
            return;
        }

        if (HasArg(args, "--selftest"))
        {
            WidgetRegistry.Initialize();
            Dispatcher.BeginInvoke(async () =>
            {
                bool passed = await SelfTest.RunAsync();
                Shutdown(passed ? 0 : 1);
            });
            return;
        }

        if (HasArg(args, "--restore-taskbar"))
        {
            var explorer = new ManagedShell.AppBar.ExplorerHelper();
            TaskbarGuard.RecoverIfNeeded(explorer);
            explorer.SetTaskbarVisibility(0x0040 /* SWP_SHOWWINDOW */);
            Shutdown();
            return;
        }

        // "Restart" passes our pid so the new instance waits for the old one to release the mutex.
        int afterPid = Array.IndexOf(args, "--after-pid");
        if (afterPid >= 0 && afterPid + 1 < args.Length && int.TryParse(args[afterPid + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(10_000); }
            catch (ArgumentException) { } // already gone
        }

        _singleInstance = new Mutex(true, @"Local\DeskBar.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("DeskBar is already running.", "DeskBar", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        StopLegacyLinkbarDesktops();
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            Log.Write($"FATAL {ex.ExceptionObject}");
            if (Shell != null) TaskbarGuard.RecoverIfNeeded(Shell.ExplorerHelper);
        };

        Log.Write($"DeskBar {typeof(App).Assembly.GetName().Version} starting");
        var settings = BarSettings.Current;

        EnvironmentHelper.IsAppRunningAsShell = Native.GetShellWindow() == IntPtr.Zero;
        var config = ShellManager.DefaultShellConfig;
        config.TaskIconSize = IconSize.Large;
        if (settings.PinnedTrayIcons != null)
            config.PinnedNotifyIcons = settings.PinnedTrayIcons.ToArray();
        Shell = new ShellManager(config);

        TaskbarGuard.RecoverIfNeeded(Shell.ExplorerHelper);
        Theme.Apply(settings);
        Desktops = new VirtualDesktopService(Dispatcher);
        Pins = new PinStore();
        WidgetRegistry.Initialize();

        _bars = new BarManager(Shell);
        _bars.Open();
        ApplyTaskbarVisibility(settings);

        BarSettings.Changed += (_, _) =>
        {
            Theme.Apply(BarSettings.Current);
            ApplyTaskbarVisibility(BarSettings.Current);
            _bars.Reopen();
        };

        // "DeskBar.exe --exit" sets this event.
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => Dispatcher.BeginInvoke(ExitGracefully), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void ApplyTaskbarVisibility(BarSettings settings)
    {
        if (settings.HideWindowsTaskbar) TaskbarGuard.Hide(Shell.ExplorerHelper);
        else TaskbarGuard.Show(Shell.ExplorerHelper);
    }

    /// <summary>Saves which tray icons are pinned (they change when the user drags or toggles them).</summary>
    public void SaveTrayPins()
    {
        var settings = BarSettings.Current;
        settings.PinnedTrayIcons = Shell.NotificationArea.PinnedNotifyIcons.ToList();
        settings.WriteFile();
    }

    public void ExitGracefully()
    {
        Shell?.AppBarManager.SignalGracefulShutdown();
        Shutdown();
    }

    public void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--after-pid {Environment.ProcessId}") { UseShellExecute = false });
        ExitGracefully();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void App_OnSessionEnding(object sender, SessionEndingCancelEventArgs e) => Cleanup();

    private void Cleanup()
    {
        if (_cleanedUp || Shell == null) return;
        _cleanedUp = true;
        Log.Write("DeskBar exiting");

        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        _bars?.Dispose();
        TaskbarGuard.Show(Shell.ExplorerHelper);
        Desktops?.Dispose();
        Shell.Dispose();
        _singleInstance?.ReleaseMutex();
    }

    private bool _errorShowing;

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (_errorShowing) return;
        _errorShowing = true;
        var answer = MessageBox.Show(
            $"DeskBar hit an error and may not work correctly:\n\n{e.Exception.Message}\n\nRestart DeskBar now? " +
            $"(Details are in {AppPaths.LogFile})",
            "DeskBar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        _errorShowing = false;
        if (answer == MessageBoxResult.Yes) Restart();
    }

    /// <summary>The previous Linkbar-based version would fight DeskBar for the screen edge; ask it to quit.</summary>
    private static void StopLegacyLinkbarDesktops()
    {
        if (!EventWaitHandle.TryOpenExisting(LegacyExitEventName, out var legacy)) return;
        using (legacy) legacy.Set();
        Log.Write("Asked the old LinkbarDesktops app to exit");
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
}
