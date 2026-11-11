using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace DeskBar.Core;

public enum BarEdge { Bottom, Top, Left, Right }

public enum SearchMode
{
    /// <summary>Type in the bar and see Everything results in a pop-up.</summary>
    Everything,
    /// <summary>Clicking the box sends a hotkey (e.g. Alt+Space for Everything / Flow Launcher).</summary>
    Hotkey,
    Hidden,
}

/// <summary>
/// Everything the user can configure. Stored as %APPDATA%\DeskBar\settings.json; unknown or missing
/// properties fall back to the defaults below, so the file can be hand-edited.
/// </summary>
public sealed class BarSettings
{
    // Appearance
    public BarEdge Edge { get; set; } = BarEdge.Bottom;
    /// <summary>Height relative to the Windows 10 taskbar (40px at 100% display scale).</summary>
    public int SizePercent { get; set; } = 100;
    /// <summary>Width (in pixels at 100% scale, before SizePercent) when the bar is on the left or right edge.</summary>
    public int VerticalWidth { get; set; } = 76;
    /// <summary>When true the bar follows each monitor's Windows display scale; otherwise FixedScalePercent is used.</summary>
    public bool MatchWindowsScale { get; set; } = true;
    public int FixedScalePercent { get; set; } = 125;
    public bool ShowWindowTitles { get; set; }
    public bool UseBlur { get; set; } = true;
    /// <summary>Bar background opacity, 0-100.</summary>
    public int BackgroundOpacity { get; set; } = 85;

    // Behaviour
    public bool AutoHide { get; set; }
    /// <summary>An auto-hidden bar reappears when the mouse comes within this many pixels (at 100% scale) of its edge.</summary>
    public int AutoHideRevealDistance { get; set; } = 12;
    /// <summary>How long (ms) an auto-hidden bar waits after the mouse leaves before sliding away.</summary>
    public int AutoHideDelayMs { get; set; } = 400;
    /// <summary>
    /// How long (ms) the bar waits after the Start menu closes before sliding away. Start vanishes at once, so
    /// the default is 0 with a quicker slide (see BarWindow.StartMenuHideAnimationMs).
    /// </summary>
    public int StartMenuHideDelayMs { get; set; }
    /// <summary>Show an auto-hidden bar while the Start menu is open (Win key), like the Windows taskbar.</summary>
    public bool ShowBarWithStartMenu { get; set; } = true;
    /// <summary>Nudge the Start menu so it doesn't cover the bar.</summary>
    public bool KeepStartMenuClearOfBar { get; set; } = true;
    public bool ShowOnAllMonitors { get; set; } = true;
    /// <summary>When true, each monitor's bar only lists windows on that monitor; by default every bar shows every app.</summary>
    public bool TasksOnOwnMonitorOnly { get; set; }
    public bool HideWindowsTaskbar { get; set; } = true;

    // Clock
    public bool ShowSeconds { get; set; }
    public bool ShowDate { get; set; } = true;

    // Search
    public SearchMode SearchMode { get; set; } = SearchMode.Everything;
    public string SearchHotkey { get; set; } = "Alt+Space";
    /// <summary>Path to Everything.exe; empty means auto-detect.</summary>
    public string EverythingPath { get; set; } = "";
    public int SearchBoxWidth { get; set; } = 260;
    public int SearchMaxResults { get; set; } = 12;

    // Layout: widget ids in order. "tasks" stretches to fill the space between the two groups.
    public List<string> LeftWidgets { get; set; } = ["start", "search", "desktop", "tasks"];
    public List<string> RightWidgets { get; set; } = ["tray", "clock", "actioncenter", "showdesktop"];

    /// <summary>Tray icons shown on the bar instead of in the overflow. Null means ManagedShell's defaults.</summary>
    public List<string>? PinnedTrayIcons { get; set; }

    // ----------------------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static BarSettings Current { get; private set; } = Load();

    /// <summary>Raised (on the caller's thread) after <see cref="Save"/> replaces <see cref="Current"/>.</summary>
    public static event EventHandler? Changed;

    private static BarSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<BarSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("Reading settings.json; using defaults", ex);
        }
        var settings = new BarSettings();
        settings.WriteFile();
        return settings;
    }

    public BarSettings Clone() =>
        JsonSerializer.Deserialize<BarSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    /// <summary>Makes this the active settings, writes them to disk and notifies the bars.</summary>
    public void Save()
    {
        WriteFile();
        Current = this;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Persists without notifying (for state the bar itself changes, like tray icon pinning).</summary>
    public void WriteFile() => File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));

    // Not stored in the JSON: it lives in the registry Run key.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DeskBar";

    [JsonIgnore]
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
