using System.Windows.Media;
using Facetbar.Core;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTasks;

namespace Facetbar.Widgets.Tasks;

/// <summary>
/// A window's icon, falling back to its program file's icon. Some windows (often games, e.g. Cyberpunk 2077)
/// don't answer Windows' icon requests, or answer too slowly while busy, which left their buttons blank.
/// </summary>
internal static class WindowIcons
{
    private static readonly Dictionary<string, ImageSource?> ExeIcons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(ApplicationWindow? window)
    {
        if (window == null) return null;
        if (window.Icon is { } icon) return icon;
        return ExeIcon(window.WinFileName);
    }

    private static ImageSource? ExeIcon(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        if (ExeIcons.TryGetValue(exe, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            icon = IconImageConverter.GetImageFromAssociatedIcon(exe, IconSize.Large);
            icon?.Freeze();
        }
        catch (Exception ex)
        {
            Log.Write($"No icon in {exe}: {ex.Message}");
        }
        ExeIcons[exe] = icon;
        return icon;
    }
}
