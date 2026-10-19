using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Bar;
using DeskBar.Core;
using ManagedShell.Interop;
using ManagedShell.WindowsTasks;

namespace DeskBar.Widgets.Tasks;

/// <summary>Visual for one <see cref="TaskGroup"/>.</summary>
internal sealed class TaskButton : Border
{
    public const string PinDragFormat = "DeskBarPinnedApp";

    private readonly TaskListControl _owner;
    private readonly BarContext _context;
    private readonly Image _icon = new() { Width = 24, Height = 24 };
    private readonly Image _overlay = new() { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 6, 0) };
    private readonly Border _indicator = new();
    private readonly Border _progress = new() { Opacity = 0.55, RenderTransformOrigin = new Point(0, 0.5) };
    private readonly ScaleTransform _progressScale = new(0, 1);
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _dragActivateTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly List<ApplicationWindow> _subscribed = [];
    private Point? _pressPoint;
    private bool _hovered;

    public TaskGroup Group { get; private set; }

    public TaskButton(TaskListControl owner, BarContext context, TaskGroup group)
    {
        _owner = owner;
        _context = context;
        Group = group;

        bool titles = context.Settings.ShowWindowTitles;
        if (context.IsVertical)
        {
            Height = 40; // full bar width
            Margin = new Thickness(0, 0, 0, 1);
        }
        else
        {
            Width = titles ? 168 : 48;
            Margin = new Thickness(0, 0, 1, 0);
        }
        Background = Brushes.Transparent;
        _progress.RenderTransform = _progressScale;
        SnapsToDevicePixels = true;
        AllowDrop = true;

        var iconHost = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(_icon);
        iconHost.Children.Add(_overlay);

        var content = new DockPanel
        {
            HorizontalAlignment = titles ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            Margin = new Thickness(titles ? 8 : 0, 0, 0, 0),
        };
        content.Children.Add(iconHost);
        if (titles) content.Children.Add(_title);

        // The "running" line sits on the screen-edge side of the button, like Windows'.
        switch (context.Edge)
        {
            case BarEdge.Left or BarEdge.Right:
                _indicator.Width = 2;
                _indicator.HorizontalAlignment = context.Edge == BarEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                _indicator.VerticalAlignment = VerticalAlignment.Center;
                break;
            default:
                _indicator.Height = 2;
                _indicator.VerticalAlignment = context.Edge == BarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                _indicator.HorizontalAlignment = HorizontalAlignment.Center;
                break;
        }

        var layers = new Grid();
        layers.Children.Add(_progress);
        layers.Children.Add(content);
        layers.Children.Add(_indicator);
        Child = layers;

        MouseEnter += (_, _) => { _hovered = true; Refresh(); _hoverTimer.Start(); _owner.Preview.MouseEnteredButton(this); };
        MouseLeave += (_, _) => { _hovered = false; Refresh(); _hoverTimer.Stop(); _owner.Preview.MouseLeftButton(this); };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (Group.IsRunning) _owner.Preview.Show(this, fromClick: false);
        };
        _dragActivateTimer.Tick += (_, _) =>
        {
            _dragActivateTimer.Stop();
            if (Group.Windows.Count == 1) Group.Windows[0].BringToFront();
            else if (Group.IsRunning) _owner.Preview.Show(this, fromClick: true);
        };

        SetGroup(group);
    }

    public void SetGroup(TaskGroup group)
    {
        Group = group;
        foreach (var w in _subscribed) w.PropertyChanged -= OnWindowChanged;
        _subscribed.Clear();
        foreach (var w in group.Windows)
        {
            w.PropertyChanged += OnWindowChanged;
            _subscribed.Add(w);
        }
        Refresh();
    }

    private void OnWindowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ApplicationWindow.State):
            case nameof(ApplicationWindow.Icon):
            case nameof(ApplicationWindow.Title):
            case nameof(ApplicationWindow.OverlayIcon):
            case nameof(ApplicationWindow.ProgressState):
            case nameof(ApplicationWindow.ProgressValue):
                Refresh();
                break;
        }
    }

    public void Detach()
    {
        foreach (var w in _subscribed) w.PropertyChanged -= OnWindowChanged;
        _subscribed.Clear();
        _hoverTimer.Stop();
        _dragActivateTimer.Stop();
    }

    // ------------------------------------------------------------------ visuals

    public void Refresh()
    {
        var g = Group;
        var first = g.Windows.FirstOrDefault();
        _icon.Source = g.Pin?.Icon ?? first?.Icon;
        _overlay.Source = first?.OverlayIcon;
        _title.Text = g.Windows.Count == 1 ? first!.Title : g.Name;
        ToolTip = g.Windows.Count > 1 ? null : g.Tooltip; // multi-window groups show the preview instead

        bool active = g.IsActive;
        if (g.IsFlashing && !active) Background = Ui.Res("FlashBrush");
        else if (active) Background = _hovered ? Ui.Res("HoverBrush") : Ui.Res("ActiveTaskBrush");
        else Background = _hovered ? Ui.Res("HoverBrush") : Brushes.Transparent;

        _indicator.Visibility = g.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        _indicator.Background = Ui.Res("AccentBrush");
        // Full length when active/hovered, slightly shorter when just running.
        double length = _context.IsVertical ? Height : Width;
        double indicatorLength = active || _hovered ? length : length - 12;
        if (_context.IsVertical) _indicator.Height = indicatorLength;
        else _indicator.Width = indicatorLength;
        _indicator.Opacity = g.Windows.Count > 1 && !active ? 0.85 : 1;

        UpdateProgress(first);
    }

    private void UpdateProgress(ApplicationWindow? w)
    {
        var state = w?.ProgressState ?? NativeMethods.TBPFLAG.TBPF_NOPROGRESS;
        if (state == NativeMethods.TBPFLAG.TBPF_NOPROGRESS)
        {
            _progress.Visibility = Visibility.Collapsed;
            return;
        }
        _progress.Visibility = Visibility.Visible;
        _progress.Background = state switch
        {
            NativeMethods.TBPFLAG.TBPF_ERROR => Brushes.Firebrick,
            NativeMethods.TBPFLAG.TBPF_PAUSED => Brushes.Goldenrod,
            _ => Brushes.SeaGreen,
        };
        double fraction = state == NativeMethods.TBPFLAG.TBPF_INDETERMINATE ? 1
            : w!.ProgressValue <= 100 ? w.ProgressValue / 100.0 : w.ProgressValue / 65535.0;
        _progressScale.ScaleX = Math.Clamp(fraction, 0, 1); // fills from the left whatever the button's size
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressPoint = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed || Group.Pin == null) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressPoint = null;
        ReleaseMouseCapture();
        _owner.Preview.Close();
        DragDrop.DoDragDrop(this, new DataObject(PinDragFormat, Group.Pin), DragDropEffects.Move);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool wasPressed = _pressPoint != null;
        _pressPoint = null;
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        bool releasedInside = p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        if (!wasPressed || !releasedInside) return;
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || !Group.IsRunning)
        {
            Group.LaunchNew();
            return;
        }
        if (Group.Windows.Count == 1)
        {
            var w = Group.Windows[0];
            if (w.State == ApplicationWindow.WindowState.Active && !w.IsMinimized) w.Minimize();
            else w.BringToFront();
            _owner.Preview.Close();
            return;
        }
        _owner.Preview.Toggle(this);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            Group.LaunchNew();
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        e.Handled = true;
        _owner.Preview.Close();
        BuildMenu().IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { PlacementTarget = this };
        _context.TrackMenu(menu);
        var g = Group;
        var desktopId = _context.Desktops.Current.Id;

        if (!string.IsNullOrEmpty(g.Name))
            menu.Items.Add(BarWindow.MenuItem(g.Name, g.LaunchNew));

        if (g.Pin is { } pin)
        {
            var scope = _context.Pins.ScopeOf(pin, desktopId);
            var allDesktops = new MenuItem { Header = "Show on all desktops", IsChecked = scope == PinScope.AllDesktops };
            allDesktops.Click += (_, _) => _context.Pins.SetScope(pin, scope == PinScope.AllDesktops ? PinScope.ThisDesktop : PinScope.AllDesktops, desktopId);
            menu.Items.Add(allDesktops);
            menu.Items.Add(BarWindow.MenuItem("Unpin from taskbar", () => _context.Pins.Unpin(pin)));
        }
        else if (g.CreatePin() is { } newPin)
        {
            menu.Items.Add(BarWindow.MenuItem("Pin to this desktop", () => _context.Pins.Pin(newPin, PinScope.ThisDesktop, desktopId)));
            menu.Items.Add(BarWindow.MenuItem("Pin to all desktops", () => _context.Pins.Pin(newPin, PinScope.AllDesktops, desktopId)));
        }

        if (g.IsRunning)
        {
            menu.Items.Add(new Separator());
            var windows = g.Windows.ToList();
            menu.Items.Add(BarWindow.MenuItem(windows.Count == 1 ? "Close window" : $"Close all {windows.Count} windows", () =>
            {
                foreach (var w in windows) w.Close();
            }));
        }
        return menu;
    }

    // ------------------------------------------------------------------ drag & drop of files / other pins

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!e.Data.GetDataPresent(PinDragFormat) && Group.IsRunning) _dragActivateTimer.Start();
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _dragActivateTimer.Stop();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        _dragActivateTimer.Stop();
    }
}
