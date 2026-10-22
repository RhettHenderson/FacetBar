using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DeskBar.Core;

/// <summary>Pushes the Windows accent colour and the bar opacity into the application resources.</summary>
internal static class Theme
{
    public static void Apply(BarSettings settings)
    {
        var accent = ReadAccentColor();
        var resources = Application.Current.Resources;
        resources["AccentColor"] = accent;
        resources["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        resources["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x60, accent.R, accent.G, accent.B)));
        // Task buttons: a light accent tint marks running apps, a stronger one the focused/hovered app.
        resources["RunningTaskBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)));
        resources["ActiveTaskBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B)));

        byte alpha = (byte)Math.Round(Math.Clamp(settings.BackgroundOpacity, 0, 100) * 2.55);
        resources["BarBackground"] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, 0x16, 0x16, 0x16)));
    }

    private static Color ReadAccentColor()
    {
        // DWM\AccentColor is stored as 0xAABBGGRR.
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is int abgr)
            return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        return Color.FromRgb(0x00, 0x78, 0xD7);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
