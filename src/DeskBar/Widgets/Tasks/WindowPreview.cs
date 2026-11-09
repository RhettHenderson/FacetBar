using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Core;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace DeskBar.Widgets.Tasks;

/// <summary>
/// The flyout that appears over a task button: a live DWM thumbnail per window (or a plain list when there
/// are many). Hovering a thumbnail peeks at that window; clicking switches to it.
/// </summary>
internal sealed class WindowPreview
{
    private const int MaxThumbnails = 6;
    private const double ThumbWidth = 200, ThumbHeight = 120;

    private readonly BarContext _context;
    private readonly Popup _popup;
    private readonly Border _root;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<(IntPtr Thumb, FrameworkElement Host, ApplicationWindow Window)> _thumbs = [];
    private TaskButton? _button;
    private ApplicationWindow? _peekTarget;
    private bool _peeking;
    private bool _openedByClick;

    public WindowPreview(BarContext context)
    {
        _context = context;
        _root = new Border { Style = (Style)Application.Current.FindResource("PopupBorder"), Padding = new Thickness(6) };
        _popup = new Popup
        {
            Placement = context.PopupPlacement,
            StaysOpen = true,
            AllowsTransparency = false, // DWM thumbnails need a normal (non-layered) window
            PopupAnimation = PopupAnimation.None,
            Child = _root,
        };
        context.TrackPopup(_popup);
        _root.MouseEnter += (_, _) => _closeTimer.Stop();
        _root.MouseLeave += (_, _) => StartCloseTimer();
        _closeTimer.Tick += (_, _) => Close();
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            if (_peekTarget == null) return;
            WindowHelper.PeekWindow(true, _peekTarget.Handle, _context.Bar.Handle);
            _peeking = true;
        };
        _popup.Closed += (_, _) =>
        {
            UnregisterThumbnails();
            EndPeek();
            _button = null;
        };
    }

    public bool IsOpenFor(TaskButton button) => _popup.IsOpen && _button == button;

    public void Toggle(TaskButton button)
    {
        if (IsOpenFor(button) && _openedByClick) Close();
        else Show(button, fromClick: true);
    }

    public void Show(TaskButton button, bool fromClick)
    {
        _closeTimer.Stop();
        if (IsOpenFor(button))
        {
            _openedByClick |= fromClick;
            return;
        }
        Close();
        _button = button;
        _openedByClick = fromClick;
        Build(button.Group);
        _popup.PlacementTarget = button;
        _popup.IsOpen = true;
        // Thumbnails are registered once the popup has an HWND and its layout is final.
        _popup.Dispatcher.BeginInvoke(RegisterThumbnails, DispatcherPriority.Loaded);
    }

    /// <summary>Re-renders after the group's windows changed (e.g. one was closed from the flyout).</summary>
    public void Refresh(TaskButton button)
    {
        if (!IsOpenFor(button)) return;
        if (!button.Group.IsRunning) { Close(); return; }
        UnregisterThumbnails();
        Build(button.Group);
        _popup.Dispatcher.BeginInvoke(RegisterThumbnails, DispatcherPriority.Loaded);
    }

    public void Close()
    {
        _closeTimer.Stop();
        _popup.IsOpen = false;
    }

    public void MouseEnteredButton(TaskButton button)
    {
        if (_button == button) _closeTimer.Stop();
    }

    public void MouseLeftButton(TaskButton button)
    {
        if (_button == button) StartCloseTimer();
    }

    /// <summary>
    /// The flyout closes shortly after the mouse leaves both it and its button, whether it was opened by
    /// hovering or by clicking (a click-opened one gets a little longer). Otherwise a click-opened flyout
    /// stayed up indefinitely and held an auto-hidden bar on screen.
    /// </summary>
    private void StartCloseTimer()
    {
        _closeTimer.Stop();
        _closeTimer.Interval = TimeSpan.FromMilliseconds(_openedByClick ? 700 : 350);
        _closeTimer.Start();
    }

    // ------------------------------------------------------------------ content

    private void Build(TaskGroup group)
    {
        var windows = group.Windows.ToList();
        bool thumbnails = windows.Count <= MaxThumbnails;
        // Thumbnails line up along the bar: side by side on a horizontal bar, stacked on a vertical one.
        var panel = new StackPanel { Orientation = thumbnails && !_context.IsVertical ? Orientation.Horizontal : Orientation.Vertical };
        foreach (var w in windows)
            panel.Children.Add(thumbnails ? ThumbnailItem(w) : ListItem(w));
        _root.Child = panel;
    }

    private FrameworkElement ThumbnailItem(ApplicationWindow w)
    {
        var host = new Border { Width = ThumbWidth, Height = ThumbHeight, Background = Brushes.Transparent, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(Header(w, ThumbWidth));
        stack.Children.Add(host);
        return Item(w, stack, host);
    }

    private FrameworkElement ListItem(ApplicationWindow w) => Item(w, Header(w, 360), null);

    private static FrameworkElement Header(ApplicationWindow w, double width)
    {
        var grid = new Grid { Width = width };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Image { Source = WindowIcons.For(w), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) });
        var title = new TextBlock { Text = w.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var close = Ui.GlyphButton("", "Close", 24, w.Close, 10);
        close.Height = 24;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return grid;
    }

    private FrameworkElement Item(ApplicationWindow w, FrameworkElement content, FrameworkElement? thumbHost)
    {
        var item = new Border { Padding = new Thickness(8), Background = Brushes.Transparent, Child = content, Tag = w };
        item.MouseEnter += (_, _) =>
        {
            item.Background = Ui.Res("HoverBrush");
            _peekTarget = w;
            _peekTimer.Stop();
            _peekTimer.Start();
        };
        item.MouseLeave += (_, _) =>
        {
            item.Background = Brushes.Transparent;
            _peekTimer.Stop();
            EndPeek();
        };
        item.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d)) return; // the close button
            EndPeek();
            w.BringToFront();
            Close();
        };
        item.MouseUp += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) w.Close();
        };
        return item;
    }

    private static bool IsInsideButton(DependencyObject d)
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c))
            if (c is ButtonBase) return true;
        return false;
    }

    private void EndPeek()
    {
        _peekTimer.Stop();
        _peekTarget = null;
        if (!_peeking) return;
        WindowHelper.PeekWindow(false, IntPtr.Zero, _context.Bar.Handle);
        _peeking = false;
    }

    // ------------------------------------------------------------------ DWM thumbnails

    private void RegisterThumbnails()
    {
        if (!_popup.IsOpen || _root.Child is not Panel panel) return;
        if (PresentationSource.FromVisual(_root) is not HwndSource source) return;

        double dpi = VisualTreeHelper.GetDpi(_root).DpiScaleX;
        foreach (var item in panel.Children.OfType<Border>())
        {
            if (item.Child is not StackPanel { Children.Count: 2 } stack || stack.Children[1] is not FrameworkElement host) continue;
            if (item.Tag is not ApplicationWindow window) continue;
            if (Native.DwmRegisterThumbnail(source.Handle, window.Handle, out var thumb) != 0) continue;

            var bounds = host.TransformToAncestor(source.RootVisual).TransformBounds(new Rect(host.RenderSize));
            Native.DwmQueryThumbnailSourceSize(thumb, out var size);
            var dest = Fit(bounds, size, dpi);
            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = dest,
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
            _thumbs.Add((thumb, host, window));
        }
    }

    /// <summary>Largest rect with the source's aspect ratio that fits (centred) in the host, in physical pixels.</summary>
    private static Native.RECT Fit(Rect host, Native.SIZE source, double dpi)
    {
        double w = host.Width * dpi, h = host.Height * dpi;
        if (source.cx > 0 && source.cy > 0)
        {
            double scale = Math.Min(w / source.cx, h / source.cy);
            scale = Math.Min(scale, 1.0 * dpi); // don't blow tiny windows up
            w = source.cx * scale;
            h = source.cy * scale;
        }
        double left = host.X * dpi + (host.Width * dpi - w) / 2;
        double top = host.Y * dpi + (host.Height * dpi - h) / 2;
        return new Native.RECT((int)left, (int)top, (int)(left + w), (int)(top + h));
    }

    private void UnregisterThumbnails()
    {
        foreach (var (thumb, _, _) in _thumbs) Native.DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }
}
