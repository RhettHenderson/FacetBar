using System.Runtime.CompilerServices;
using ManagedShell.Common.Helpers;
using ManagedShell.UWPInterop;
using ManagedShell.WindowsTasks;

namespace DeskBar.Core;

/// <summary>
/// Store/MSIX apps (new Outlook, Claude, ...) live in versioned folders under Program Files\WindowsApps that
/// change on every update and can't be launched by path. These helpers find their AppUserModelID instead.
/// </summary>
internal static class PackagedApps
{
    private static bool _fetched;
    private static readonly ConditionalWeakTable<ApplicationWindow, StrongBox<string?>> WindowIds = new();

    public static bool IsPackagedPath(string? path) =>
        path != null && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>"...\WindowsApps\Microsoft.OutlookForWindows_1.2026.922.300_x64__8wekyb3d8bbwe\olk.exe" → "Microsoft.OutlookForWindows_8wekyb3d8bbwe".</summary>
    public static string? FamilyNameFromPath(string path)
    {
        const string marker = @"\WindowsApps\";
        int start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += marker.Length;
        int end = path.IndexOf('\\', start);
        var folder = end < 0 ? path[start..] : path[start..end];

        // Package folder: Name_Version_Architecture_ResourceId_PublisherId (package names can't contain '_').
        var parts = folder.Split('_');
        return parts.Length >= 3 ? $"{parts[0]}_{parts[^1]}" : null;
    }

    /// <summary>App id of the installed package that the given (possibly outdated) path belongs to.</summary>
    public static string? AppIdForPath(string path)
    {
        var family = FamilyNameFromPath(path);
        if (family == null) return null;
        try
        {
            if (!_fetched)
            {
                StoreAppHelper.AppList.FetchApps();
                _fetched = true;
            }
            return StoreAppHelper.AppList
                .Select(a => a.AppUserModelId)
                .FirstOrDefault(id => id.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Error($"Looking up packaged app for {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// The window's app id: the package's id for Store apps (read from the process), otherwise an id the
    /// window set explicitly (Chrome profiles etc.), otherwise null. Cached per window.
    /// </summary>
    public static string? AppIdForWindow(ApplicationWindow window) =>
        WindowIds.GetValue(window, w =>
        {
            string? id = null;
            try { id = ShellHelper.GetAppUserModelIdForHandle(w.Handle); }
            catch (Exception ex) { Log.Write($"No app id for window {w.Handle}: {ex.Message}"); }
            return new StrongBox<string?>(string.IsNullOrWhiteSpace(id) ? null : id);
        }).Value;
}
