using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private int _openPopups;

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

    public void AddOpenPopup()
    {
        bool before = AllowAutoHide;
        _openPopups++;
        if (AllowAutoHide != before) OnPropertyChanged(nameof(AllowAutoHide));
    }

    public void RemoveOpenPopup()
    {
        bool before = AllowAutoHide;
        _openPopups = Math.Max(0, _openPopups - 1);
        if (AllowAutoHide != before) OnPropertyChanged(nameof(AllowAutoHide));
    }

    protected override bool ShouldAllowAutoHide() =>
        _openPopups == 0 && !IsKeyboardFocusWithin && base.ShouldAllowAutoHide();

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        OnPropertyChanged(nameof(AllowAutoHide));
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
