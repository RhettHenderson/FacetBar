using System.Windows.Threading;
using DeskBar.Core;
using ManagedShell;
using ManagedShell.AppBar;

namespace DeskBar.Bar;

/// <summary>Opens a bar on each monitor (or just the primary) and recreates them when displays or settings change.</summary>
internal sealed class BarManager : IDisposable
{
    private readonly ShellManager _shell;
    private readonly List<BarWindow> _bars = [];
    private readonly DispatcherTimer _displayDebounce;
    private readonly DispatcherTimer _explorerDebounce;
    private List<AppBarScreen> _screens = [];

    public BarManager(ShellManager shell)
    {
        _shell = shell;
        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _displayDebounce.Tick += (_, _) =>
        {
            _displayDebounce.Stop();
            if (ScreensChanged()) Reopen();
        };
        _explorerDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _explorerDebounce.Tick += (_, _) =>
        {
            _explorerDebounce.Stop();
            Log.Write("Explorer restarted; reopening bars");
            Reopen();
        };

        App.Current.Desktops.CurrentChanged += (_, _) =>
        {
            foreach (var bar in _bars) bar.Peek(); // show auto-hidden bars briefly so the new desktop's pins are visible
        };

        _startMenu = new StartMenuMonitor(Dispatcher.CurrentDispatcher);
        _startMenu.Changed += OnStartMenuChanged;
        // Start animates in, and may settle its position after it first appears, so check twice.
        _startNudge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _startNudge.Tick += (_, _) =>
        {
            if (++_startNudgeCount >= 3) _startNudge.Stop();
            HandleOpenStartMenu(log: _startNudgeCount == 1);
        };
    }

    private readonly StartMenuMonitor _startMenu;
    private readonly DispatcherTimer _startNudge;
    private int _startNudgeCount;

    private void OnStartMenuChanged(object? sender, EventArgs e)
    {
        Log.Write($"Start menu {(_startMenu.IsOpen ? "opened" : "closed")}");
        if (!_startMenu.IsOpen)
        {
            _startNudge.Stop();
            foreach (var bar in _bars) bar.SetStartMenuOpen(false);
            return;
        }
        HandleOpenStartMenu(log: false);
        _startNudgeCount = 0;
        _startNudge.Start();
    }

    /// <summary>Shows the bar on the Start menu's monitor and nudges Start off the bar if it overlaps.</summary>
    private void HandleOpenStartMenu(bool log)
    {
        if (!_startMenu.IsOpen) return;
        var settings = BarSettings.Current;
        var start = StartMenuMonitor.FindStartWindow();
        var monitor = start != IntPtr.Zero
            ? Native.MonitorFromWindow(start, Native.MONITOR_DEFAULTTOPRIMARY)
            : _bars.FirstOrDefault(b => b.Screen.Primary)?.Screen.HMonitor ?? IntPtr.Zero;

        foreach (var each in _bars)
            each.SetStartMenuOpen(settings.ShowBarWithStartMenu && each.Screen.HMonitor == monitor);

        if (start == IntPtr.Zero)
        {
            if (log) Log.Write("Start menu window not found");
            return;
        }
        if (!settings.KeepStartMenuClearOfBar) return;

        var bounds = StartMenuMonitor.GetBounds(start);
        var bar = _bars.FirstOrDefault(b => b.Screen.HMonitor == monitor);
        if (bounds is not { } s || bar == null || !Native.GetWindowRect(bar.Handle, out var b)) return;

        // How far Start must move to stop overlapping the bar, away from the bar's edge.
        bool overlapsAlong = s.Left < b.Right && s.Right > b.Left && s.Top < b.Bottom && s.Bottom > b.Top;
        int dx = 0, dy = 0;
        if (overlapsAlong)
        {
            switch (settings.Edge)
            {
                case BarEdge.Right: dx = b.Left - s.Right; break;
                case BarEdge.Left: dx = b.Right - s.Left; break;
                case BarEdge.Top: dy = b.Bottom - s.Top; break;
                default: dy = b.Top - s.Bottom; break;
            }
        }
        if (dx == 0 && dy == 0)
        {
            if (log) Log.Write($"Start menu at {s.Left},{s.Top}-{s.Right},{s.Bottom} clears the bar at {b.Left},{b.Top}-{b.Right},{b.Bottom}");
            return;
        }

        bool moved = StartMenuMonitor.Move(start, dx, dy);
        var after = StartMenuMonitor.GetBounds(start);
        Log.Write($"Start menu overlapped the bar (start {s.Left},{s.Top}-{s.Right},{s.Bottom}, bar {b.Left},{b.Top}-{b.Right},{b.Bottom}); " +
                  $"moved by {dx},{dy}: {(moved ? "ok" : $"refused (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})")}" +
                  (after is { } a ? $", now {a.Left},{a.Top}-{a.Right},{a.Bottom}" : ""));
    }

    public IReadOnlyList<BarWindow> Bars => _bars;

    public void Open()
    {
        var settings = BarSettings.Current;
        _screens = AppBarScreen.FromAllScreens();
        var targets = settings.ShowOnAllMonitors ? _screens : [AppBarScreen.FromPrimaryScreen()];
        foreach (var screen in targets)
        {
            Log.Write($"Opening bar on {screen.DeviceName} ({screen.Bounds}){(screen.Primary ? " [primary]" : "")}");
            var bar = new BarWindow(this, _shell, screen, settings);
            bar.Show();
            _bars.Add(bar);
        }
    }

    public void Reopen()
    {
        CloseAll();
        Open();
    }

    public void OnDisplaysChanged()
    {
        _displayDebounce.Stop();
        _displayDebounce.Start();
    }

    public void OnExplorerRestarted()
    {
        _explorerDebounce.Stop();
        _explorerDebounce.Start();
    }

    private bool ScreensChanged()
    {
        var now = AppBarScreen.FromAllScreens();
        return now.Count != _screens.Count || now.Zip(_screens).Any(p =>
            p.First.Bounds != p.Second.Bounds || p.First.DeviceName != p.Second.DeviceName || p.First.Primary != p.Second.Primary);
    }

    private void CloseAll()
    {
        foreach (var bar in _bars)
        {
            bar.AllowClose = true;
            bar.Close();
        }
        _bars.Clear();
    }

    public void Dispose()
    {
        _displayDebounce.Stop();
        _explorerDebounce.Stop();
        _startNudge.Stop();
        _startMenu.Dispose();
        CloseAll();
    }
}
