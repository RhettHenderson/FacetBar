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
        if (pin.TargetExe == null || WindowExe(w) is not { } exe) return false;
        return pin.TargetExe == exe || IsRenamedCopy(pin.TargetExe, exe) || pin.TargetExe == LauncherExe(w);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ApplicationWindow, System.Runtime.CompilerServices.StrongBox<string?>> Launchers = new();

    /// <summary>
    /// For windows owned by a helper process (Steam's steamwebhelper.exe), the program that started it
    /// (steam.exe), lower-cased; otherwise null. Cached per window.
    /// </summary>
    public static string? LauncherExe(ApplicationWindow w) =>
        Launchers.GetValue(w, window =>
        {
            string? launcher = null;
            try
            {
                if (window.ProcId is { } pid) launcher = ProcessTree.LauncherExe(pid)?.ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Log.Write($"Couldn't trace the launcher of window {window.Handle}: {ex.Message}");
            }
            return new System.Runtime.CompilerServices.StrongBox<string?>(launcher);
        }).Value;

    private static readonly Dictionary<string, string?> OriginalNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a running exe is the pinned exe under another name. Build tools and self-updaters often
    /// rename a running exe (e.g. Zing.exe → Zing.prev.exe) to write a new one in its place, and Windows
    /// then reports the renamed path for the old process. The file's version info still records its
    /// original name. This is only a fallback, because some apps (e.g. Electron ones) record "electron.exe" there.
    /// </summary>
    internal static bool IsRenamedCopy(string pinExe, string windowExe)
    {
        if (!string.Equals(Path.GetDirectoryName(pinExe), Path.GetDirectoryName(windowExe), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!OriginalNames.TryGetValue(windowExe, out var original))
        {
            try { original = System.Diagnostics.FileVersionInfo.GetVersionInfo(windowExe).OriginalFilename; }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { original = null; }
            OriginalNames[windowExe] = original;
        }
        return !string.IsNullOrWhiteSpace(original)
               && string.Equals(original.Trim(), Path.GetFileName(pinExe), StringComparison.OrdinalIgnoreCase);
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
        // Pin the real program, not a helper process that happens to own the window (Steam).
        var pin = PinnedApp.FromPath(LauncherExe(w) is { } launcher ? ProperCasePath(launcher, exe) : exe);
        pin.AppUserModelId = appId; // keeps apps with explicit ids (Chrome profiles, etc.) matched correctly
        return pin;
    }

    /// <summary>
    /// Repairs a pin that points at a helper process (e.g. a Steam pin made from its window before DeskBar
    /// knew about helpers) to point at the program that launches it. Returns true if the pin changed.
    /// </summary>
    public static bool TryRetargetHelperPin(PinnedApp pin, ApplicationWindow w)
    {
        if (pin.IsPackaged || pin.TargetExe == null || pin.TargetExe != WindowExe(w)) return false;
        if (LauncherExe(w) is not { } launcher || launcher == pin.TargetExe) return false;
        var target = ProperCasePath(launcher, null);
        Log.Write($"Pin '{pin.Name}' pointed at helper {pin.Path}; now launches {target}");
        pin.RetargetTo(target);
        return true;
    }

    /// <summary>Launcher paths are cached lower-case; get the on-disk casing back for display and storage.</summary>
    private static string ProperCasePath(string path, string? fallback)
    {
        try { return new FileInfo(path).Exists ? Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path)).FirstOrDefault() ?? path : fallback ?? path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
    }
}
