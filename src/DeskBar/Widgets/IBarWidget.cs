using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using DeskBar.Bar;
using DeskBar.Core;
using DeskBar.Widgets.Tasks;
using ManagedShell;
using ManagedShell.AppBar;

namespace DeskBar.Widgets;

/// <summary>
/// A piece of the bar (start button, clock, ...). Widgets are listed by <see cref="Id"/> in
/// settings.json (LeftWidgets / RightWidgets). To add one, implement this interface; either register it in
/// <see cref="WidgetRegistry"/> or build it into a DLL that references DeskBar.dll and drop it in
/// %APPDATA%\DeskBar\Plugins.
/// </summary>
public interface IBarWidget
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Only makes sense once (e.g. the notification area): shown on the primary monitor's bar only.</summary>
    bool PrimaryMonitorOnly => false;

    /// <summary>Takes up the space left over between the left and right groups (the task list does).</summary>
    bool Stretch => false;

    /// <summary>
    /// Builds the widget's UI for one bar. Called once per bar each time bars are (re)created. If the
    /// returned element implements IDisposable it is disposed when the bar closes.
    /// </summary>
    FrameworkElement Create(BarContext context);
}

/// <summary>What a widget can use from the bar it lives in.</summary>
public sealed class BarContext
{
    internal BarContext(BarWindow bar, ShellManager shell, VirtualDesktopService desktops, PinStore pins)
    {
        Bar = bar;
        Shell = shell;
        Desktops = desktops;
        Pins = pins;
    }

    public BarWindow Bar { get; }
    public ShellManager Shell { get; }
    public VirtualDesktopService Desktops { get; }
    public PinStore Pins { get; }
    public BarSettings Settings => BarSettings.Current;
    public AppBarScreen Screen => Bar.Screen;
    public bool IsPrimary => Bar.Screen.Primary;
    public BarEdge Edge => Settings.Edge;

    /// <summary>True when the bar runs down the left or right side of the screen.</summary>
    public bool IsVertical => Edge is BarEdge.Left or BarEdge.Right;

    /// <summary>Popups should open away from the screen edge the bar is on.</summary>
    public PlacementMode PopupPlacement => Edge switch
    {
        BarEdge.Top => PlacementMode.Bottom,
        BarEdge.Left => PlacementMode.Right,
        BarEdge.Right => PlacementMode.Left,
        _ => PlacementMode.Top,
    };

    /// <summary>Sets an element's size along the bar (width on a horizontal bar, height on a vertical one).</summary>
    public T Along<T>(T element, double length) where T : FrameworkElement
    {
        if (IsVertical) element.Height = length;
        else element.Width = length;
        return element;
    }

    /// <summary>Thickness that puts <paramref name="value"/> on the side facing the rest of the screen.</summary>
    public Thickness InnerEdge(double value) => Edge switch
    {
        BarEdge.Top => new Thickness(0, 0, 0, value),
        BarEdge.Left => new Thickness(0, 0, value, 0),
        BarEdge.Right => new Thickness(value, 0, 0, 0),
        _ => new Thickness(0, value, 0, 0),
    };

    /// <summary>Keeps an auto-hiding bar visible while the popup is open.</summary>
    public void TrackPopup(Popup popup)
    {
        Bar.RegisterPopup(popup);
    }

    /// <summary>Same as <see cref="TrackPopup"/> for context menus opened from code.</summary>
    public void TrackMenu(System.Windows.Controls.ContextMenu menu)
    {
        Bar.RegisterMenu(menu);
    }
}

public static class WidgetRegistry
{
    private static readonly Dictionary<string, IBarWidget> Widgets = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<IBarWidget> All => Widgets.Values;

    public static IBarWidget? Find(string id) => Widgets.GetValueOrDefault(id);

    public static void Register(IBarWidget widget) => Widgets[widget.Id] = widget;

    internal static void Initialize()
    {
        Register(new StartButtonWidget());
        Register(new SearchWidget());
        Register(new TaskListWidget());
        Register(new TrayWidget());
        Register(new ClockWidget());
        Register(new ActionCenterWidget());
        Register(new ShowDesktopWidget());
        Register(new DesktopNameWidget());
        Register(new SpacerWidget());
        LoadPlugins();
    }

    private static void LoadPlugins()
    {
        if (!Directory.Exists(AppPaths.PluginsDir)) return;
        foreach (var dll in Directory.GetFiles(AppPaths.PluginsDir, "*.dll"))
        {
            try
            {
                var assembly = Assembly.LoadFrom(dll);
                foreach (var type in assembly.GetTypes().Where(t =>
                             typeof(IBarWidget).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null))
                {
                    var widget = (IBarWidget)Activator.CreateInstance(type)!;
                    Register(widget);
                    Log.Write($"Loaded plugin widget '{widget.Id}' from {Path.GetFileName(dll)}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Loading plugin {dll}", ex);
            }
        }
    }
}
