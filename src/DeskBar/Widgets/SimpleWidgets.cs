using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskBar.Core;
using ManagedShell.Common.Helpers;

namespace DeskBar.Widgets;

internal static class Ui
{
    public static Style ButtonStyle => (Style)Application.Current.FindResource("BarButton");
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Glyph(string glyph, double size = 16) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button GlyphButton(string glyph, string tooltip, double width, Action onClick, double glyphSize = 16)
    {
        var button = new Button
        {
            Style = ButtonStyle,
            Width = width,
            Content = Glyph(glyph, glyphSize),
            ToolTip = tooltip,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}

/// <summary>Windows logo; opens the Start menu (right-click: the Win+X power menu).</summary>
internal sealed class StartButtonWidget : IBarWidget
{
    public string Id => "start";
    public string DisplayName => "Start button";

    public FrameworkElement Create(BarContext context)
    {
        var logo = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 H7.2 V7.2 H0 Z M8.8,0 H16 V7.2 H8.8 Z M0,8.8 H7.2 V16 H0 Z M8.8,8.8 H16 V16 H8.8 Z"),
            Fill = Ui.Res("ForegroundBrush"),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };
        var button = new Button { Style = Ui.ButtonStyle, Width = 48, Content = logo, ToolTip = "Start" };
        button.MouseEnter += (_, _) => logo.Fill = Ui.Res("AccentBrush");
        button.MouseLeave += (_, _) => logo.Fill = Ui.Res("ForegroundBrush");
        button.Click += (_, _) => ShellHelper.ShowStartMenu();
        button.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShellHelper.ShowStartContextMenu();
        };
        return button;
    }
}

/// <summary>Task View button that also shows the current virtual desktop's name.</summary>
internal sealed class DesktopNameWidget : IBarWidget
{
    public string Id => "desktop";
    public string DisplayName => "Task View + desktop name";

    public FrameworkElement Create(BarContext context) => new DesktopNameButton(context);

    private sealed class DesktopNameButton : Button, IDisposable
    {
        private readonly BarContext _context;
        private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

        public DesktopNameButton(BarContext context)
        {
            _context = context;
            Style = Ui.ButtonStyle;
            Padding = new Thickness(12, 0, 12, 0);
            ToolTip = "Task View (Win+Tab)";
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(Ui.Glyph(""));
            panel.Children.Add(_name);
            Content = panel;
            Click += (_, _) => ShellHelper.ShowWindowSwitcher();
            Update(this, EventArgs.Empty);
            context.Desktops.CurrentChanged += Update;
        }

        private void Update(object? sender, EventArgs e) => _name.Text = _context.Desktops.Current.Name;

        public void Dispose() => _context.Desktops.CurrentChanged -= Update;
    }
}

/// <summary>Opens the Windows Action Center (Win+A).</summary>
internal sealed class ActionCenterWidget : IBarWidget
{
    public string Id => "actioncenter";
    public string DisplayName => "Action Center button";
    public bool PrimaryMonitorOnly => true;

    public FrameworkElement Create(BarContext context) =>
        Ui.GlyphButton("", "Action Center", 44, ShellHelper.ShowActionCenter);
}

/// <summary>Thin strip at the far end that toggles the desktop, like Windows'.</summary>
internal sealed class ShowDesktopWidget : IBarWidget
{
    public string Id => "showdesktop";
    public string DisplayName => "Show desktop strip";

    public FrameworkElement Create(BarContext context)
    {
        var button = new Button
        {
            Style = Ui.ButtonStyle,
            Width = 8,
            BorderBrush = Ui.Res("SeparatorBrush"),
            ToolTip = "Show desktop",
        };
        button.Content = new Border
        {
            Width = 8,
            BorderBrush = Ui.Res("SeparatorBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Background = Brushes.Transparent,
        };
        button.Click += (_, _) => ToggleDesktop();
        return button;
    }

    public static void ToggleDesktop()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            dynamic? shell = type != null ? Activator.CreateInstance(type) : null;
            shell?.ToggleDesktop();
        }
        catch (Exception ex)
        {
            Log.Error("Toggling desktop", ex);
        }
    }
}

/// <summary>Fixed gap, for hand-tuned layouts in settings.json.</summary>
internal sealed class SpacerWidget : IBarWidget
{
    public string Id => "spacer";
    public string DisplayName => "Spacer";

    public FrameworkElement Create(BarContext context) => new Border { Width = 12, Background = Brushes.Transparent };
}
