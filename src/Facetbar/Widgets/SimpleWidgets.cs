using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Facetbar.Core;
using ManagedShell.Common.Helpers;

namespace Facetbar.Widgets;

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
            Focusable = false, // clicking bar buttons shouldn't steal focus (e.g. from the search box)
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A glyph button sized along the bar: <paramref name="length"/> wide on a horizontal bar, tall on a vertical one.</summary>
    public static Button BarGlyphButton(BarContext context, string glyph, string tooltip, double length, Action onClick, double glyphSize = 16)
    {
        var button = GlyphButton(glyph, tooltip, length, onClick, glyphSize);
        if (context.IsVertical)
        {
            button.Width = double.NaN;
            button.Height = length;
        }
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
        var button = context.Along(new Button { Style = Ui.ButtonStyle, Content = logo, ToolTip = "Start" }, context.IsVertical ? 40 : 48);
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
        private readonly TextBlock _name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        public DesktopNameButton(BarContext context)
        {
            _context = context;
            Style = Ui.ButtonStyle;
            ToolTip = "Task View (Win+Tab)";
            var panel = new StackPanel();
            panel.Children.Add(Ui.Glyph(""));
            panel.Children.Add(_name);
            if (context.IsVertical)
            {
                // Narrow bar: glyph above a small, centred name.
                Padding = new Thickness(4, 8, 4, 8);
                _name.FontSize = 11;
                _name.Margin = new Thickness(0, 4, 0, 0);
                _name.HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                Padding = new Thickness(12, 0, 12, 0);
                panel.Orientation = Orientation.Horizontal;
                _name.Margin = new Thickness(8, 0, 0, 0);
            }
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
        Ui.BarGlyphButton(context, "", "Action Center", context.IsVertical ? 40 : 44, ShellHelper.ShowActionCenter);
}

/// <summary>Thin strip at the far end that toggles the desktop, like Windows'.</summary>
internal sealed class ShowDesktopWidget : IBarWidget
{
    public string Id => "showdesktop";
    public string DisplayName => "Show desktop strip";

    public FrameworkElement Create(BarContext context)
    {
        var button = context.Along(new Button
        {
            Style = Ui.ButtonStyle,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            ToolTip = "Show desktop",
        }, 8);
        // A thin line separates the strip from the rest of the bar (on its left, or on top when vertical).
        button.Content = context.Along(new Border
        {
            BorderBrush = Ui.Res("SeparatorBrush"),
            BorderThickness = context.IsVertical ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0),
            Background = Brushes.Transparent,
        }, 8);
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

    public FrameworkElement Create(BarContext context) => context.Along(new Border { Background = Brushes.Transparent }, 12);
}
