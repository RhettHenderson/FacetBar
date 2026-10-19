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
        CloseAll();
    }
}
