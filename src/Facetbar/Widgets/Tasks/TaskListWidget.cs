using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Facetbar.Core;
using ManagedShell.WindowsTasks;

namespace Facetbar.Widgets.Tasks;

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
    private readonly StackPanel _panel = new();
    private readonly ScrollViewer _scroll;
    private readonly Dictionary<object, TaskButton> _buttons = [];
    private readonly Dictionary<IntPtr, long> _firstSeen = [];
    private readonly HashSet<ApplicationWindow> _watched = [];
    /// <summary>Pins already checked for pointing at a helper process (shared by all bars).</summary>
    private static readonly HashSet<PinnedApp> RetargetChecked = [];
    private long _sequence;
    private bool _rebuildQueued;

    public WindowPreview Preview { get; }

    public TaskListControl(BarContext context)
    {
        _context = context;
        Preview = new WindowPreview(context);
        Background = Brushes.Transparent; // so empty space accepts drops and the bar's context menu
        AllowDrop = true;

        bool vertical = context.IsVertical;
        _panel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Margin = vertical ? new Thickness(0, 2, 0, 2) : new Thickness(2, 0, 2, 0);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled,
            Content = _panel,
            Focusable = false,
        };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (vertical) _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta / 2.0);
            else _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        Child = _scroll;

        // Each bar gets its own view of ManagedShell's window list. ManagedShell turns on WPF "live shaping"
        // for these views, which has a bug: a delayed RestoreLiveShaping can run after the view is discarded
        // (when bars are rebuilt) and throw a NullReferenceException. So switch live shaping off and filter
        // ourselves, re-running whenever a window's ShowInTaskbar or monitor changes.
        _windows = context.Shell.Tasks.CreateGroupedWindowsCollection();
        if (_windows is ICollectionViewLiveShaping live)
        {
            live.IsLiveFiltering = false;
            live.IsLiveGrouping = false;
            live.IsLiveSorting = false;
        }
        _windows.GroupDescriptions.Clear();
        _windows.Filter = null;
        ((INotifyCollectionChanged)_windows).CollectionChanged += OnSourcesChanged;
        context.Pins.Changed += OnSourcesChanged;
        context.Desktops.CurrentChanged += OnSourcesChanged;

        Rebuild();
    }

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Windows on other virtual desktops are "cloaked", which flips ShowInTaskbar when you switch desktops.
        if (e.PropertyName is nameof(ApplicationWindow.ShowInTaskbar) or nameof(ApplicationWindow.HMonitor))
            OnSourcesChanged(sender, e);
    }

    /// <summary>Listens to every window in the list (shown or not), so we notice when one should appear.</summary>
    private void UpdateWatchedWindows(List<ApplicationWindow> all)
    {
        foreach (var w in all.Where(w => _watched.Add(w)))
            w.PropertyChanged += OnWindowPropertyChanged;
        foreach (var gone in _watched.Except(all).ToList())
        {
            gone.PropertyChanged -= OnWindowPropertyChanged;
            _watched.Remove(gone);
        }
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
        var all = _windows.Cast<ApplicationWindow>().ToList();
        UpdateWatchedWindows(all);
        var windows = all.Where(IncludeWindow).ToList();

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

        // One-time repair of pins that point at a helper process (e.g. a Steam pin made from steamwebhelper.exe).
        foreach (var group in groups.Where(g => g.Pin != null && g.IsRunning && RetargetChecked.Add(g.Pin)))
        {
            if (TaskGroup.TryRetargetHelperPin(group.Pin!, group.Windows[0]))
                _context.Pins.Updated(group.Pin!);
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
            var p = e.GetPosition(button);
            bool past = _context.IsVertical ? p.Y > button.ActualHeight / 2 : p.X > button.ActualWidth / 2;
            if (past) index++;
            else break;
        }
        return index;
    }

    public void Dispose()
    {
        Preview.Close();
        foreach (var w in _watched) w.PropertyChanged -= OnWindowPropertyChanged;
        _watched.Clear();
        ((INotifyCollectionChanged)_windows).CollectionChanged -= OnSourcesChanged;
        _context.Pins.Changed -= OnSourcesChanged;
        _context.Desktops.CurrentChanged -= OnSourcesChanged;
        if (_windows is ListCollectionView view) view.DetachFromSourceCollection();
        foreach (var b in _buttons.Values) b.Detach();
        _buttons.Clear();
    }
}
