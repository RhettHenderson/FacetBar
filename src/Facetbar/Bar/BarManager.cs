using System.Windows.Threading;
using Facetbar.Core;
using ManagedShell;
using ManagedShell.AppBar;

namespace Facetbar.Bar;

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

        _startMenu = new StartMenuMonitor();
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

        var bar = _bars.FirstOrDefault(b => b.Screen.HMonitor == monitor);
        if (bar == null || !Native.GetWindowRect(bar.Handle, out var b)) return;
        PlaceStartWindow(start, bar, b, settings, log);

        // Search (typing in Start) has its own panel-sized window: move it only if it actually overlaps.
        if (!settings.KeepStartMenuClearOfBar) return;
        foreach (var (search, _) in StartMenuMonitor.FindShellWindows().Where(w => !w.IsStart))
        {
            if (!Native.GetWindowRect(search, out var s)) continue;
            if (!(s.Left < b.Right && s.Right > b.Left && s.Top < b.Bottom && s.Bottom > b.Top)) continue;
            int dx = 0, dy = 0;
            switch (settings.Edge)
            {
                case BarEdge.Right: dx = b.Left - s.Right; break;
                case BarEdge.Left: dx = b.Right - s.Left; break;
                case BarEdge.Top: dy = b.Bottom - s.Top; break;
                default: dy = b.Top - s.Bottom; break;
            }
            bool ok = StartMenuMonitor.Move(search, dx, dy);
            Log.Write($"Search panel {s.Left},{s.Top}-{s.Right},{s.Bottom} overlapped the bar; moved by {dx},{dy}: {(ok ? "ok" : "refused")}");
        }
    }

    /// <summary>Where Start's window was before Facetbar moved it, so it can be put back on exit.</summary>
    private (IntPtr Handle, int X, int Y)? _startOrigin;

    /// <summary>
    /// Start's window covers its whole monitor and Windows draws the menu inside it, beside the (hidden)
    /// Windows taskbar. The window is invisible but sits above every other window, Facetbar included. When
    /// Facetbar is on the same edge as the Windows taskbar, slide the whole window in by the bar's width so
    /// neither it nor the menu covers the bar. Windows remembers the position, so set it exactly each time.
    /// </summary>
    private void PlaceStartWindow(IntPtr start, BarWindow bar, Native.RECT b, BarSettings settings, bool log)
    {
        if (!Native.GetWindowRect(start, out var s)) return;
        var screen = bar.Screen.Bounds;
        int x = screen.Left, y = screen.Top;
        var taskbarEdge = StartMenuMonitor.ExplorerTaskbarEdge();
        if (settings.KeepStartMenuClearOfBar && taskbarEdge == settings.Edge)
        {
            switch (settings.Edge)
            {
                case BarEdge.Right: x = screen.Left - (screen.Right - b.Left); break;
                case BarEdge.Left: x = screen.Left + (b.Right - screen.Left); break;
                case BarEdge.Top: y = screen.Top + (b.Bottom - screen.Top); break;
                default: y = screen.Top - (screen.Bottom - b.Top); break;
            }
        }

        if (s.Left == x && s.Top == y)
        {
            if (log) Log.Write($"Start menu window already at {x},{y} (Windows taskbar edge {taskbarEdge?.ToString() ?? "?"}, bar {b.Left},{b.Top}-{b.Right},{b.Bottom})");
            return;
        }

        bool ok = StartMenuMonitor.MoveTo(start, x, y);
        int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        bool atOrigin = x == screen.Left && y == screen.Top;
        _startOrigin = ok && !atOrigin ? (start, screen.Left, screen.Top) : null;
        Log.Write($"Start menu window {s.Left},{s.Top}-{s.Right},{s.Bottom} moved to {x},{y} " +
                  $"(Windows taskbar edge {taskbarEdge?.ToString() ?? "?"}, bar {b.Left},{b.Top}-{b.Right},{b.Bottom}): " +
                  (ok ? "ok" : $"refused (error {error})"));
    }

    /// <summary>Puts Start's window back where Windows had it (on exit, so Start isn't left offset).</summary>
    private void RestoreStartWindow()
    {
        if (_startOrigin is not { } o) return;
        bool ok = StartMenuMonitor.MoveTo(o.Handle, o.X, o.Y);
        Log.Write($"Start menu window restored to {o.X},{o.Y}: {(ok ? "ok" : "refused")}");
        _startOrigin = null;
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
        RestoreStartWindow();
        CloseAll();
    }
}
