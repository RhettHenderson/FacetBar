using System.Diagnostics;

namespace LinkbarDesktops;

internal sealed class TrayContext : ApplicationContext
{
    private readonly BarManager _manager;
    private readonly NotifyIcon _tray;
    private readonly VirtualDesktopWatcher _watcher = new();
    private readonly ShellEventsWindow _shellEvents;
    private readonly System.Windows.Forms.Timer _taskbarRecreatedDelay = new() { Interval = 2000 };
    private readonly EventWaitHandle _exitEvent = new(false, EventResetMode.AutoReset, Program.ExitEventName);
    private readonly RegisteredWaitHandle _exitRequested;
    private bool _exiting;

    public TrayContext(string linkbarExe)
    {
        _manager = new BarManager(linkbarExe);
        _manager.CurrentChanged += UpdateTrayText;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        _tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(linkbarExe) ?? SystemIcons.Application,
            Text = "Linkbar Desktops",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) OpenFolder(CurrentLinksDir());
        };

        // Linkbar re-registers all its bars when Explorer restarts; wait for that to finish, then fix them up.
        _taskbarRecreatedDelay.Tick += (_, _) =>
        {
            _taskbarRecreatedDelay.Stop();
            _manager.OnTaskbarRecreated();
        };
        _shellEvents = new ShellEventsWindow(() =>
        {
            Log.Write("Explorer restarted (TaskbarCreated)");
            _taskbarRecreatedDelay.Stop();
            _taskbarRecreatedDelay.Start();
        });

        // Creating the controls above installed the WinForms SynchronizationContext.
        var ui = SynchronizationContext.Current!;
        _watcher.Changed += () => ui.Post(_ => _manager.RequestSync(), null);

        // "LinkbarDesktops.exe --exit" sets this event.
        _exitRequested = ThreadPool.RegisterWaitForSingleObject(_exitEvent,
            (_, _) => ui.Post(async _ => await ExitAsync(), null), null, Timeout.Infinite, executeOnlyOnce: true);

        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _manager.StartAsync();
            _watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"Startup failed: {ex}");
            MessageBox.Show($"Linkbar Desktops failed to start:\n\n{ex.Message}", "Linkbar Desktops",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            await ExitAsync();
        }
    }

    private void UpdateTrayText()
    {
        var text = $"Linkbar Desktops - {_manager.Current?.Name}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private string? CurrentLinksDir() =>
        _manager.Current is { } d ? BarManager.LinksDirFor(d.Id) : null;

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var bars = new ToolStripMenuItem("Open links folder for");
        foreach (var desktop in _manager.Desktops)
        {
            var dir = BarManager.LinksDirFor(desktop.Id);
            var item = new ToolStripMenuItem(desktop.Name, null, (_, _) => OpenFolder(dir))
            {
                Checked = desktop.Id == _manager.Current?.Id,
                Enabled = Directory.Exists(dir),
            };
            bars.DropDownItems.Add(item);
        }

        menu.Items.Add(new ToolStripMenuItem($"Current: {_manager.Current?.Name ?? "?"}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(bars);
        menu.Items.Add("Copy this bar's style to all desktops", null, async (_, _) =>
        {
            var answer = MessageBox.Show(
                "Copy this desktop's bar appearance and position (not its links) to every other desktop's bar?\n\n" +
                "The other bars will restart. Desktops you haven't visited yet will also get this style.",
                "Linkbar Desktops", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.OK) await _manager.CopyCurrentStyleToAllAsync();
        });
        menu.Items.Add("Restart all bars", null, async (_, _) => await _manager.RestartAllAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open data folder", null, (_, _) => OpenFolder(AppSettings.DataDir));
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (s, _) =>
        {
            AppSettings.StartWithWindows = !AppSettings.StartWithWindows;
        })
        { Checked = AppSettings.StartWithWindows });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (closes all bars)", null, async (_, _) => await ExitAsync());
    }

    private static void OpenFolder(string? dir)
    {
        if (dir == null || !Directory.Exists(dir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _exitRequested.Unregister(null);
        _exitEvent.Dispose();
        _watcher.Dispose();
        await _manager.ShutdownAsync();
        _tray.Visible = false;
        _tray.Dispose();
        _shellEvents.DestroyHandle();
        ExitThread();
    }

    /// <summary>Hidden top-level window that receives the "TaskbarCreated" broadcast.</summary>
    private sealed class ShellEventsWindow : NativeWindow
    {
        private readonly uint _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
        private readonly Action _onTaskbarCreated;

        public ShellEventsWindow(Action onTaskbarCreated)
        {
            _onTaskbarCreated = onTaskbarCreated;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _taskbarCreated) _onTaskbarCreated();
            base.WndProc(ref m);
        }
    }
}
