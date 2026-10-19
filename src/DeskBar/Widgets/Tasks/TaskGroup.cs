using DeskBar.Core;
using ManagedShell.WindowsTasks;

namespace DeskBar.Widgets.Tasks;

/// <summary>One button on the task list: a pinned app and/or the running windows that belong to it.</summary>
public sealed class TaskGroup
{
    public TaskGroup(object key, PinnedApp? pin)
    {
        Key = key;
        Pin = pin;
    }

    /// <summary>The <see cref="PinnedApp"/> instance for pinned groups, otherwise the app id / exe path string.</summary>
    public object Key { get; }
    public PinnedApp? Pin { get; set; }
    public List<ApplicationWindow> Windows { get; } = [];

    public bool IsRunning => Windows.Count > 0;
    public bool IsActive => Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
    public bool IsFlashing => Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);

    public string Name
    {
        get
        {
            if (Pin != null) return Pin.Name;
            var first = Windows.FirstOrDefault();
            if (first == null) return "";
            return !string.IsNullOrWhiteSpace(first.WinFileDescription) ? first.WinFileDescription : first.Title;
        }
    }

    public string Tooltip => Windows.Count == 1 ? Windows[0].Title : Name;

    public void LaunchNew()
    {
        // An unpinned app is started the same way a pin for it would be (by app id for Store apps).
        (Pin ?? CreatePin())?.Launch();
    }

    // ------------------------------------------------------------------ matching windows to apps

    public static string? WindowAppId(ApplicationWindow w) => PackagedApps.AppIdForWindow(w);

    public static string? WindowExe(ApplicationWindow w) =>
        string.IsNullOrWhiteSpace(w.WinFileName) ? null : w.WinFileName.ToLowerInvariant();

    /// <summary>Key used to group running windows that aren't pinned.</summary>
    public static string GroupKey(ApplicationWindow w) => WindowAppId(w) ?? WindowExe(w) ?? $"hwnd:{w.Handle}";

    public static bool Matches(PinnedApp pin, ApplicationWindow w)
    {
        var pinId = pin.EffectiveAppId;
        var windowId = WindowAppId(w);
        if (pinId != null && windowId != null)
            return string.Equals(pinId, windowId, StringComparison.OrdinalIgnoreCase);
        return pin.TargetExe != null && pin.TargetExe == WindowExe(w);
    }

    /// <summary>Builds a pin for a running app (used by "Pin to ..." on an unpinned button).</summary>
    public PinnedApp? CreatePin()
    {
        var w = Windows.FirstOrDefault();
        if (w == null) return null;
        var appId = WindowAppId(w);
        var exe = w.WinFileName;
        if (w.IsUWP || PackagedApps.IsPackagedPath(exe))
            return appId == null ? null : new PinnedApp { AppUserModelId = appId, Name = Name }; // launched by id, survives updates
        if (string.IsNullOrEmpty(exe)) return null;
        var pin = PinnedApp.FromPath(exe);
        pin.AppUserModelId = appId; // keeps apps with explicit ids (Chrome profiles, etc.) matched correctly
        return pin;
    }
}
