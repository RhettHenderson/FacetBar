using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Core;
using DeskBar.Widgets;
using ManagedShell;
using ManagedShell.AppBar;
using ManagedShell.Common.Helpers;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;

namespace DeskBar.Bar;

/// <summary>One bar on one monitor. Hosts the widgets listed in settings in a left group, a stretching middle and a right group.</summary>
public sealed class BarWindow : AppBarWindow
{
    /// <summary>The Windows 10 taskbar is 40 DIPs tall (40px at 100% scale).</summary>
    public const double BaseHeight = 40;

    private readonly ShellManager _shell;
    private readonly BarManager _manager;
    private readonly Border _root;
    private readonly Grid _layout;
    private readonly List<FrameworkElement> _widgetElements = [];
    private readonly uint _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _fullScreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<Popup> _popups = [];
    private readonly HashSet<ContextMenu> _menus = [];
    private bool _mouseNearEdge;
    private bool _mouseOverBar;
    private bool _hidden;
    private bool? _lastAllowAutoHide;
    private bool _applyingFullScreen;

    public BarContext Context { get; }

    internal BarWindow(BarManager manager, ShellManager shell, AppBarScreen screen, BarSettings settings)
        : base(shell.AppBarManager, shell.ExplorerHelper, shell.FullScreenHelper, screen,
            settings.Edge switch
            {
                BarEdge.Top => AppBarEdge.Top,
                BarEdge.Left => AppBarEdge.Left,
                BarEdge.Right => AppBarEdge.Right,
                _ => AppBarEdge.Bottom,
            },
            settings.AutoHide ? AppBarMode.AutoHide : AppBarMode.Normal,
            settings.Edge is BarEdge.Left or BarEdge.Right ? settings.VerticalWidth : BaseHeight)
    {
        _manager = manager;
        _shell = shell;
        Context = new BarContext(this, shell, App.Current.Desktops, App.Current.Pins);

        Title = "DeskBar";
        AllowsTransparency = true; // needed for blur and for click-through while auto-hidden
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;
        Foreground = (Brush)FindResource("ForegroundBrush");

        _layout = new Grid { ClipToBounds = true };
        _root = new Border
        {
            Background = (Brush)FindResource("BarBackground"),
            BorderBrush = (Brush)FindResource("SeparatorBrush"),
            BorderThickness = Context.InnerEdge(1),
            Child = _layout,
            ContextMenu = BuildContextMenu(),
        };
        Content = _root;
        AutoHideElement = _root;
        AutoHideShowMargin = 2;

        ApplyScale();
        BuildWidgets(settings);

        PropertyChanged += OnBarPropertyChanged;
    }

    // ---------------------------------------------------------------- scaling

    /// <summary>
    /// Extra scale applied on top of the monitor's own DPI scaling. With "match Windows scale" the bar is
    /// already scaled by WPF, so only the size percentage applies; otherwise we undo the monitor's DPI and
    /// apply the fixed percentage instead.
    /// </summary>
    private double ContentScale
    {
        get
        {
            var s = BarSettings.Current;
            double dpiPart = s.MatchWindowsScale ? 1.0 : s.FixedScalePercent / 100.0 / Math.Max(DpiScale, 0.5);
            return dpiPart * Math.Clamp(s.SizePercent, 50, 300) / 100.0;
        }
    }

    private void ApplyScale()
    {
        double scale = ContentScale;
        _layout.LayoutTransform = new ScaleTransform(scale, scale);
        bool changed;
        if (Context.IsVertical)
        {
            double width = Math.Round(Math.Clamp(BarSettings.Current.VerticalWidth, 40, 400) * scale);
            changed = Math.Abs(width - DesiredWidth) > 0.1;
            DesiredWidth = width;
        }
        else
        {
            double height = Math.Round(BaseHeight * scale);
            changed = Math.Abs(height - DesiredHeight) > 0.1;
            DesiredHeight = height;
        }
        if (changed && Handle != IntPtr.Zero && !IsOpening) UpdatePosition(); // while opening, the base class positions us
    }

    private void OnBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DpiScale))
        {
            ApplyScale();
            UseLayoutRounding = DpiScale % 1 == 0; // rounding mis-sizes things at fractional scales like 125%
        }
    }

    // ---------------------------------------------------------------- widgets

    private void BuildWidgets(BarSettings settings)
    {
        var left = Resolve(settings.LeftWidgets);
        var right = Resolve(settings.RightWidgets);

        // Slots: start-group widgets (auto), one star slot (a stretching widget or empty space), end-group widgets (auto).
        // Horizontal bars lay slots out as columns (left → right), vertical bars as rows (top → bottom).
        var stretch = left.Concat(right).FirstOrDefault(w => w.Stretch);
        bool vertical = Context.IsVertical;
        int slot = 0;

        void AddSlot(bool star)
        {
            var size = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            if (vertical) _layout.RowDefinitions.Add(new RowDefinition { Height = size });
            else _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = size });
        }

        void Add(IBarWidget widget, bool star)
        {
            AddSlot(star);
            try
            {
                var element = widget.Create(Context);
                if (vertical) Grid.SetRow(element, slot);
                else Grid.SetColumn(element, slot);
                _layout.Children.Add(element);
                _widgetElements.Add(element);
            }
            catch (Exception ex)
            {
                Log.Error($"Creating widget '{widget.Id}'", ex);
            }
            slot++;
        }

        foreach (var w in left) Add(w, w == stretch);
        if (stretch == null)
        {
            AddSlot(star: true);
            slot++;
        }
        foreach (var w in right) Add(w, w == stretch);
    }

    private List<IBarWidget> Resolve(IEnumerable<string> ids)
    {
        var result = new List<IBarWidget>();
        foreach (var id in ids)
        {
            var widget = WidgetRegistry.Find(id);
            if (widget == null) { Log.Write($"Unknown widget '{id}' in settings"); continue; }
            if (widget.PrimaryMonitorOnly && !Screen.Primary) continue;
            result.Add(widget);
        }
        return result;
    }

    // ---------------------------------------------------------------- auto-hide

    // Whether an auto-hidden bar may slide away is decided from live state, re-checked every 40ms, rather than
    // from ManagedShell's event-driven flags (mouse enter/leave, context menu opening/closing). Those can miss
    // an event, e.g. a MouseLeave during a drag or a right-click menu that isn't the bar's own, and then the
    // bar stays visible indefinitely.

    /// <summary>Keeps the bar visible while the popup is open.</summary>
    public void RegisterPopup(Popup popup)
    {
        _popups.Add(popup);
        popup.Opened += (_, _) => ReevaluateAutoHide();
        popup.Closed += (_, _) => ReevaluateAutoHide();
    }

    /// <summary>Keeps the bar visible while a context menu opened from code is showing.</summary>
    public void RegisterMenu(ContextMenu menu)
    {
        _menus.Add(menu);
        menu.Opened += (_, _) => ReevaluateAutoHide();
        menu.Closed += (_, _) =>
        {
            _menus.Remove(menu);
            ReevaluateAutoHide();
        };
    }

    private bool AnyPopupOpen =>
        _popups.Any(p => p.IsOpen) || _menus.Any(m => m.IsOpen) || _root.ContextMenu?.IsOpen == true;

    /// <summary>Only typing (the search box) holds the bar open; a clicked button keeping focus doesn't.</summary>
    private bool IsTyping => IsKeyboardFocusWithin && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase;

    protected override bool ShouldAllowAutoHide() =>
        AppBarMode == AppBarMode.AutoHide && !_mouseOverBar && !_mouseNearEdge && !AnyPopupOpen && !IsTyping;

    private void ReevaluateAutoHide()
    {
        if (AppBarMode != AppBarMode.AutoHide || IsOpening || IsClosing) return;
        bool allow = AllowAutoHide;
        if (allow == _lastAllowAutoHide) return;
        _lastAllowAutoHide = allow;
        OnPropertyChanged(nameof(AllowAutoHide)); // the base class animates in or out
    }

    protected override void OnAutoHideAnimationBegin(bool isHiding)
    {
        base.OnAutoHideAnimationBegin(isHiding);
        _hidden = isHiding;
    }

    /// <summary>
    /// Polled: is the mouse over the visible bar, or close to the screen edge it's hidden against? Reveals an
    /// auto-hidden bar when the mouse comes within AutoHideRevealDistance of the edge, rather than only when
    /// it touches the 2px sliver that's left on screen.
    /// </summary>
    private void AutoHideTick()
    {
        if (!Native.GetCursorPos(out var pt)) return;

        // The window keeps its full size while hidden (only its content slides away), so only count the
        // mouse as "over the bar" while the bar is actually showing.
        _mouseOverBar = !_hidden && Native.GetWindowRect(Handle, out var r)
                        && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

        var b = Screen.Bounds; // physical pixels, same space as the cursor
        double reach = Math.Max(1, BarSettings.Current.AutoHideRevealDistance) * DpiScale;

        // Don't pop up over real full-screen content (we've conceded the top spot to it).
        bool near = Topmost && (Context.Edge switch
        {
            BarEdge.Top => pt.Y < b.Top + reach,
            BarEdge.Left => pt.X < b.Left + reach,
            BarEdge.Right => pt.X >= b.Right - reach,
            _ => pt.Y >= b.Bottom - reach,
        }) && (Context.IsVertical
            ? pt.Y >= b.Top && pt.Y < b.Bottom && pt.X >= b.Left - reach && pt.X < b.Right + reach
            : pt.X >= b.Left && pt.X < b.Right && pt.Y >= b.Top - reach && pt.Y < b.Bottom + reach);

        _mouseNearEdge = near;
        ReevaluateAutoHide();
    }

    // ---------------------------------------------------------------- full-screen apps

    // ManagedShell counts any window covering the whole monitor as full screen, including a maximized window
    // when an auto-hidden bar reserves no space. We only step aside for real full-screen content (videos,
    // games): those windows aren't maximized, whereas apps like a maximized Claude window are.

    protected override void OnFullScreenEnter(FullScreenApp app)
    {
        if (_applyingFullScreen) base.OnFullScreenEnter(app);
    }

    protected override void OnFullScreenLeave()
    {
        if (_applyingFullScreen) base.OnFullScreenLeave();
    }

    private void UpdateFullScreenState()
    {
        if (IsClosing) return;
        var app = _fullScreenHelper.FullScreenApps.FirstOrDefault(a =>
            (a.screen.DeviceName == Screen.DeviceName || a.screen.IsVirtualScreen) && !Native.IsZoomed(a.hWnd));
        _applyingFullScreen = true;
        try
        {
            if (app != null && Topmost) OnFullScreenEnter(app);
            else if (app == null && !Topmost) OnFullScreenLeave();
        }
        finally
        {
            _applyingFullScreen = false;
        }
    }

    private void OnFullScreenAppsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateFullScreenState();

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        ReevaluateAutoHide();
    }

    /// <summary>Briefly reveals an auto-hidden bar (e.g. after switching desktops).</summary>
    public void Peek() => PeekDuringAutoHide(1200);

    // ---------------------------------------------------------------- window plumbing

    protected override void OnSourceInitialized(object sender, EventArgs e)
    {
        base.OnSourceInitialized(sender, e);
        UseLayoutRounding = DpiScale % 1 == 0;
        SetBlur(BarSettings.Current.UseBlur);
        WindowHelper.ExcludeWindowFromPeek(Handle);
        if (Screen.Primary) SetTrayHost();

        _fullScreenHelper.FullScreenApps.CollectionChanged += OnFullScreenAppsChanged;
        // A window can go from maximized to full screen (or back) without the list changing, so re-check now and then.
        _fullScreenTimer.Tick += (_, _) => UpdateFullScreenState();
        _fullScreenTimer.Start();
        UpdateFullScreenState();

        if (AppBarMode == AppBarMode.AutoHide)
        {
            _edgeTimer.Tick += (_, _) => AutoHideTick();
            _edgeTimer.Start();
        }
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        if (msg == (int)_taskbarCreatedMessage && Screen.Primary)
            _manager.OnExplorerRestarted();
        return IntPtr.Zero;
    }

    protected override void SetScreenProperties(ScreenSetupReason reason)
    {
        if (reason == ScreenSetupReason.DpiChange)
        {
            UpdatePosition();
            return;
        }
        _manager.OnDisplaysChanged();
    }

    /// <summary>Tells tray apps where the notification area is so their flyouts open next to it.</summary>
    public void SetTrayHost()
    {
        _shell.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = (NativeMethods.ABEdge)AppBarEdge,
            rc = new NativeMethods.Rect
            {
                Top = (int)(Top * DpiScale),
                Left = (int)(Left * DpiScale),
                Bottom = (int)((Top + ActualHeight) * DpiScale),
                Right = (int)((Left + ActualWidth) * DpiScale),
            },
        });
    }

    protected override void CustomClosing()
    {
        if (!AllowClose) return;
        PropertyChanged -= OnBarPropertyChanged;
        _edgeTimer.Stop();
        _fullScreenTimer.Stop();
        _fullScreenHelper.FullScreenApps.CollectionChanged -= OnFullScreenAppsChanged;
        foreach (var element in _widgetElements.OfType<IDisposable>())
        {
            try { element.Dispose(); }
            catch (Exception ex) { Log.Error("Disposing widget", ex); }
        }
        _widgetElements.Clear();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Task Manager", ShellHelper.StartTaskManager));
        menu.Items.Add(MenuItem("Show the desktop", ShowDesktopWidget.ToggleDesktop));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("DeskBar settings…", () => Settings.SettingsWindow.ShowSingle()));
        menu.Items.Add(MenuItem("Open DeskBar data folder", () => ShellHelper.StartProcess(AppPaths.DataDir)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Restart DeskBar", () => App.Current.Restart()));
        menu.Items.Add(MenuItem("Exit DeskBar", () => App.Current.ExitGracefully()));
        return menu;
    }

    internal static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Moves keyboard focus into the bar (needed before typing in the search box).</summary>
    public void ActivateForInput()
    {
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Keyboard.ClearFocus();
    }
}
