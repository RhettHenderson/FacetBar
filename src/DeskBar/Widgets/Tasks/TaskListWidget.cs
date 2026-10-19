using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Core;
using ManagedShell.WindowsTasks;

namespace DeskBar.Widgets.Tasks;

/// <summary>Pinned apps for the current virtual desktop merged with running windows, like the Windows 10 taskbar.</summary>
internal sealed class TaskListWidget : IBarWidget
{
    public string Id => "tasks";
    public string DisplayName => "Pinned and running apps";
    public bool Stretch => true;

    public FrameworkElement Create(BarContext context) => new TaskListControl(context);
}

internal sealed class TaskListControl : Border, IDisposable
{
    private readonly BarContext _context;
    private readonly ICollectionView _windows;
    private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<object, TaskButton> _buttons = [];
    private readonly Dictionary<IntPtr, long> _firstSeen = [];
    private long _sequence;
    private bool _rebuildQueued;

    public WindowPreview Preview { get; }

    public TaskListControl(BarContext context)
    {
        _context = context;
        Preview = new WindowPreview(context);
        Background = Brushes.Transparent; // so empty space accepts drops and the bar's context menu
        AllowDrop = true;
        Margin = new Thickness(2, 0, 2, 0);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _panel,
            Focusable = false,
        };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        Child = _scroll;

        // Each bar gets its own view of ManagedShell's window list so it can filter by monitor.
        // Windows on other virtual desktops are "cloaked", which ManagedShell already filters out.
        _windows = context.Shell.Tasks.CreateGroupedWindowsCollection();
        _windows.Filter = IncludeWindow;
        ((INotifyCollectionChanged)_windows).CollectionChanged += OnSourcesChanged;
        context.Pins.Changed += OnSourcesChanged;
        context.Desktops.CurrentChanged += OnSourcesChanged;

        Rebuild();
    }

    private bool IncludeWindow(object item)
    {
        if (item is not ApplicationWindow { ShowInTaskbar: true } w) return false;
        var s = _context.Settings;
        if (!s.ShowOnAllMonitors || !s.TasksOnOwnMonitorOnly) return true;
        return w.HMonitor == _context.Screen.HMonitor;
    }

    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var pins = _context.Pins.GetPins(_context.Desktops.Current.Id);
        var windows = _windows.Cast<ApplicationWindow>().ToList();

        foreach (var w in windows)
            _firstSeen.TryAdd(w.Handle, _sequence++);
        foreach (var gone in _firstSeen.Keys.Except(windows.Select(w => w.Handle)).ToList())
            _firstSeen.Remove(gone);

        // Pinned apps first (in pin order), then unpinned apps in the order their first window appeared.
        var groups = pins.Select(p => new TaskGroup(p, p)).ToList();
        var byKey = new Dictionary<string, TaskGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows.OrderBy(w => _firstSeen[w.Handle]))
        {
            var group = groups.FirstOrDefault(g => g.Pin != null && TaskGroup.Matches(g.Pin, w));
            if (group == null)
            {
                var key = TaskGroup.GroupKey(w);
                if (!byKey.TryGetValue(key, out group))
                {
                    group = new TaskGroup(key, null);
                    byKey[key] = group;
                    groups.Add(group);
                }
            }
            group.Windows.Add(w);
        }

        // Reuse existing buttons so hover state and previews survive updates.
        var ordered = new List<TaskButton>(groups.Count);
        var keep = new HashSet<object>();
        foreach (var group in groups)
        {
            if (_buttons.TryGetValue(group.Key, out var button)) button.SetGroup(group);
            else _buttons[group.Key] = button = new TaskButton(this, _context, group);
            keep.Add(group.Key);
            ordered.Add(button);
        }
        foreach (var stale in _buttons.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            if (Preview.IsOpenFor(_buttons[stale])) Preview.Close();
            _buttons[stale].Detach();
            _buttons.Remove(stale);
        }

        if (!_panel.Children.Cast<UIElement>().SequenceEqual(ordered))
        {
            _panel.Children.Clear();
            foreach (var b in ordered) _panel.Children.Add(b);
        }
        foreach (var b in ordered) Preview.Refresh(b);
    }

    // ------------------------------------------------------------------ drag & drop

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data.GetDataPresent(TaskButton.PinDragFormat)) e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Link;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        var desktopId = _context.Desktops.Current.Id;
        int index = InsertIndex(e);

        if (e.Data.GetData(TaskButton.PinDragFormat) is PinnedApp pin)
        {
            _context.Pins.Move(pin, index, desktopId);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            // Dropped files/shortcuts are pinned to the current desktop where they were dropped.
            foreach (var file in files)
            {
                var newPin = PinnedApp.FromPath(file);
                _context.Pins.Pin(newPin, PinScope.ThisDesktop, desktopId);
                _context.Pins.Move(newPin, index++, desktopId);
            }
        }
        e.Handled = true;
    }

    /// <summary>Position among the pinned buttons where a drop at the mouse would land.</summary>
    private int InsertIndex(DragEventArgs e)
    {
        int index = 0;
        foreach (var button in _panel.Children.OfType<TaskButton>().Where(b => b.Group.Pin != null))
        {
            var x = e.GetPosition(button).X;
            if (x > button.ActualWidth / 2) index++;
            else break;
        }
        return index;
    }

    public void Dispose()
    {
        Preview.Close();
        ((INotifyCollectionChanged)_windows).CollectionChanged -= OnSourcesChanged;
        _context.Pins.Changed -= OnSourcesChanged;
        _context.Desktops.CurrentChanged -= OnSourcesChanged;
        if (_windows is ListCollectionView view) view.DetachFromSourceCollection();
        foreach (var b in _buttons.Values) b.Detach();
        _buttons.Clear();
    }
}
