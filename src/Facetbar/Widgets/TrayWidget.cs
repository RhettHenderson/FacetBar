using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Facetbar.Core;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace Facetbar.Widgets;

/// <summary>
/// Notification area: icons you've chosen to always show, plus a ^ button with the rest ("hidden icons").
/// Drag icons between the bar and the flyout to choose, like Windows 10.
/// </summary>
internal sealed class TrayWidget : IBarWidget
{
    public string Id => "tray";
    public string DisplayName => "Notification area (tray icons)";
    public bool PrimaryMonitorOnly => true;

    public FrameworkElement Create(BarContext context) => new TrayControl(context);
}

internal sealed class TrayControl : StackPanel, IDisposable
{
    public const string IconDragFormat = "FacetbarTrayIcon";

    private readonly BarContext _context;
    private readonly NotificationArea _area;
    // A WrapPanel so icons form rows on a narrow vertical bar (on a horizontal bar it's one row).
    private readonly WrapPanel _pinned = new() { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, AllowDrop = true, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly WrapPanel _overflowIcons = new() { Width = 5 * 40, Background = Brushes.Transparent, AllowDrop = true, MinHeight = 40 };
    private readonly Popup _overflow;
    private readonly Border _overflowBorder;
    private readonly Button _chevron;
    private readonly BalloonPopup _balloon;

    public TrayControl(BarContext context)
    {
        _context = context;
        _area = context.Shell.NotificationArea;
        Orientation = context.IsVertical ? Orientation.Vertical : Orientation.Horizontal;

        // The ^ arrow points away from the screen edge the bar is on.
        string chevronGlyph = context.Edge switch
        {
            BarEdge.Top => "",
            BarEdge.Left => "",
            BarEdge.Right => "",
            _ => "",
        };
        _chevron = Ui.BarGlyphButton(context, chevronGlyph, "Show hidden icons", context.IsVertical ? 28 : 24,
            () => _overflow!.IsOpen = !_overflow.IsOpen, glyphSize: 12);
        // Dragging an icon onto the ^ button opens the flyout (and dropping on the button hides the icon).
        _chevron.AllowDrop = true;
        _chevron.DragEnter += (_, e) => { if (e.Data.GetDataPresent(IconDragFormat)) _overflow!.IsOpen = true; };
        _chevron.DragOver += (_, e) => DragOverTarget(e, null);
        _chevron.Drop += (_, e) => DropOn(e, pin: false);

        _overflowBorder = new Border
        {
            Style = (Style)Application.Current.FindResource("PopupBorder"),
            Padding = new Thickness(4),
            Child = _overflowIcons,
            AllowDrop = true,
        };
        _overflow = new Popup
        {
            PlacementTarget = _chevron,
            Placement = context.PopupPlacement,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = _overflowBorder,
        };
        context.TrackPopup(_overflow);

        Children.Add(_chevron);
        Children.Add(_pinned);

        // Drop targets: the whole tray area pins an icon to the bar; the whole flyout hides it.
        Background = Brushes.Transparent;
        AllowDrop = true;
        DragOver += (_, e) => DragOverTarget(e, _pinned);
        DragLeave += (_, _) => ClearDropHighlight();
        Drop += (_, e) => DropOn(e, pin: true);
        _overflowBorder.DragOver += (_, e) => DragOverTarget(e, _overflowIcons);
        _overflowBorder.DragLeave += (_, _) => ClearDropHighlight();
        _overflowBorder.Drop += (_, e) => DropOn(e, pin: false);

        ((INotifyCollectionChanged)_area.PinnedIcons).CollectionChanged += OnIconsChanged;
        ((INotifyCollectionChanged)_area.UnpinnedIcons).CollectionChanged += OnIconsChanged;
        Rebuild();

        _balloon = new BalloonPopup(context, this);
        _area.NotificationBalloonShown += OnBalloon;
    }

    private void OnIconsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(Rebuild, DispatcherPriority.Background);

    private void Rebuild()
    {
        Fill(_pinned, _area.PinnedIcons.Cast<NotifyIcon>());
        Fill(_overflowIcons, _area.UnpinnedIcons.Cast<NotifyIcon>());
        _chevron.Visibility = _overflowIcons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_overflowIcons.Children.Count == 0) _overflow.IsOpen = false;
    }

    private void Fill(Panel panel, IEnumerable<NotifyIcon> icons)
    {
        var wanted = icons.ToList();
        var existing = panel.Children.OfType<TrayIconElement>().ToDictionary(e => e.Icon);
        panel.Children.Clear();
        foreach (var icon in wanted)
            panel.Children.Add(existing.TryGetValue(icon, out var element) ? element : new TrayIconElement(this, icon));
        foreach (var removed in existing.Where(p => !wanted.Contains(p.Key)))
            removed.Value.Detach();
    }

    internal void HostClicked() => _context.Bar.SetTrayHost();

    /// <summary>
    /// Drags an icon (between the bar and the flyout, or to reorder). The flyout normally closes the moment
    /// it loses the mouse, which a drag causes, so it's held open until the drag finishes.
    /// </summary>
    private void RunIconDrag(TrayIconElement source)
    {
        bool flyoutWasOpen = _overflow.IsOpen;
        _overflow.StaysOpen = true;
        try
        {
            DragDrop.DoDragDrop(source, new DataObject(IconDragFormat, source.Icon), DragDropEffects.Move);
        }
        finally
        {
            ClearDropHighlight();
            // Restoring StaysOpen re-arms "close when clicking elsewhere"; close it if the drag opened it.
            if (!flyoutWasOpen && _overflow.IsOpen && !_overflowBorder.IsMouseOver) _overflow.IsOpen = false;
            _overflow.StaysOpen = false;
        }
    }

    private void DragOverTarget(DragEventArgs e, Panel? highlight)
    {
        bool ours = e.Data.GetDataPresent(IconDragFormat);
        e.Effects = ours ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        ClearDropHighlight();
        if (ours && highlight != null) highlight.Background = Ui.Res("AccentSoftBrush");
    }

    private void ClearDropHighlight()
    {
        _pinned.Background = Brushes.Transparent;
        _overflowIcons.Background = Brushes.Transparent;
    }

    private void DropOn(DragEventArgs e, bool pin)
    {
        ClearDropHighlight();
        if (e.Data.GetData(IconDragFormat) is not NotifyIcon icon) return;
        e.Handled = true;
        if (pin)
        {
            int index = 0;
            foreach (var element in _pinned.Children.OfType<TrayIconElement>())
            {
                if (element.Icon == icon) continue;
                // Count icons before the drop point in reading order (rows wrap on vertical bars).
                var p = e.GetPosition(element);
                if (p.Y > element.ActualHeight || (p.Y >= 0 && p.X > element.ActualWidth / 2)) index++;
            }
            icon.Pin(index);
        }
        else
        {
            icon.Unpin();
        }
        App.Current.SaveTrayPins();
    }

    private void OnBalloon(object? sender, NotificationBalloonEventArgs e)
    {
        if (e.Balloon.NotifyIcon == null) return;
        e.Handled = true;
        _balloon.Show(e.Balloon);
    }

    public void Dispose()
    {
        ((INotifyCollectionChanged)_area.PinnedIcons).CollectionChanged -= OnIconsChanged;
        ((INotifyCollectionChanged)_area.UnpinnedIcons).CollectionChanged -= OnIconsChanged;
        _area.NotificationBalloonShown -= OnBalloon;
        foreach (var e in _pinned.Children.OfType<TrayIconElement>().Concat(_overflowIcons.Children.OfType<TrayIconElement>()))
            e.Detach();
        _overflow.IsOpen = false;
        _balloon.Close();
    }

    /// <summary>One tray icon. Forwards mouse input to the owning app the way Explorer does.</summary>
    private sealed class TrayIconElement : Border
    {
        private readonly TrayControl _owner;
        private readonly Image _image = new() { Width = 16, Height = 16 };
        private Point? _pressPoint;

        public NotifyIcon Icon { get; }

        public TrayIconElement(TrayControl owner, NotifyIcon icon)
        {
            _owner = owner;
            Icon = icon;
            Width = 24;
            MinHeight = 32;
            Background = Brushes.Transparent;
            Child = _image;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Update();
            icon.PropertyChanged += OnIconChanged;
            MouseEnter += (_, _) => Background = Ui.Res("HoverBrush");
            MouseLeave += (_, _) => Background = Brushes.Transparent;
        }

        private void OnIconChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(NotifyIcon.Icon) or nameof(NotifyIcon.Title)) Update();
        }

        private void Update()
        {
            _image.Source = Icon.Icon;
            ToolTip = string.IsNullOrWhiteSpace(Icon.Title) ? null : Icon.Title;
        }

        public void Detach() => Icon.PropertyChanged -= OnIconChanged;

        private static uint CursorParam => MouseHelper.GetCursorPositionParam();
        private static int DoubleClickTime => System.Windows.Forms.SystemInformation.DoubleClickTime;

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            e.Handled = true;
            _owner.HostClicked();
            if (e.ChangedButton == MouseButton.Left) _pressPoint = e.GetPosition(this);
            else Icon.IconMouseDown(e.ChangedButton, CursorParam, DoubleClickTime);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left)
            {
                if (_pressPoint == null) return; // a drag happened
                _pressPoint = null;
                Icon.IconMouseDown(MouseButton.Left, CursorParam, DoubleClickTime);
            }
            Icon.IconMouseUp(e.ChangedButton, CursorParam, DoubleClickTime);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Icon.IconMouseMove(CursorParam);
            if (_pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressPoint = null;
            _owner.RunIconDrag(this);
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            // Tell the app where its icon is (Shell_NotifyIconGetRect), in physical pixels.
            var location = PointToScreen(new Point(0, 0));
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Icon.Placement = new NativeMethods.Rect
            {
                Left = (int)location.X,
                Top = (int)location.Y,
                Right = (int)(ActualWidth * dpi),
                Bottom = (int)(ActualHeight * dpi),
            };
            Icon.IconMouseEnter(CursorParam);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _pressPoint = null;
            Icon.IconMouseLeave(CursorParam);
        }
    }

    /// <summary>Classic tray balloon (Shell_NotifyIcon NIF_INFO) shown above the notification area.</summary>
    private sealed class BalloonPopup
    {
        private readonly Popup _popup;
        private readonly Image _icon = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        private readonly DispatcherTimer _timeout = new();
        private NotificationBalloon? _current;

        public BalloonPopup(BarContext context, FrameworkElement anchor)
        {
            var text = new StackPanel { Width = 300 };
            text.Children.Add(_title);
            text.Children.Add(_info);
            var dock = new DockPanel();
            var close = Ui.GlyphButton("", "Dismiss", 28, () => Dismiss(BalloonVisibility.Hidden), 10);
            close.Height = 28;
            close.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(close, Dock.Right);
            dock.Children.Add(close);
            dock.Children.Add(_icon);
            dock.Children.Add(text);
            var border = new Border
            {
                Style = (Style)Application.Current.FindResource("PopupBorder"),
                Padding = new Thickness(14, 12, 6, 14),
                Child = dock,
                Cursor = Cursors.Hand,
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
                _current?.Click();
                Dismiss(null);
            };
            _popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = context.PopupPlacement,
                StaysOpen = true,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = border,
            };
            context.TrackPopup(_popup);
            _timeout.Tick += (_, _) => Dismiss(BalloonVisibility.TimedOut);
        }

        public void Show(NotificationBalloon balloon)
        {
            Dismiss(BalloonVisibility.Hidden);
            _current = balloon;
            _title.Text = balloon.Title;
            _title.Visibility = string.IsNullOrWhiteSpace(balloon.Title) ? Visibility.Collapsed : Visibility.Visible;
            _info.Text = balloon.Info;
            _icon.Source = balloon.Icon ?? balloon.NotifyIcon.Icon;
            _timeout.Interval = TimeSpan.FromMilliseconds(Math.Clamp(balloon.Timeout, 4000, 30000));
            _timeout.Start();
            _popup.IsOpen = true;
            balloon.SetVisibility(BalloonVisibility.Visible);
        }

        private void Dismiss(BalloonVisibility? how)
        {
            _timeout.Stop();
            if (_current != null && how != null) _current.SetVisibility(how.Value);
            _current = null;
            _popup.IsOpen = false;
        }

        public void Close() => Dismiss(BalloonVisibility.Hidden);

        private static bool IsInButton(DependencyObject d)
        {
            for (var c = d; c != null; c = VisualTreeHelper.GetParent(c))
                if (c is ButtonBase) return true;
            return false;
        }
    }
}
