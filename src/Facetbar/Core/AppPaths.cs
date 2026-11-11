namespace Facetbar.Core;

internal static class AppPaths
{
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Facetbar");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string PinsFile => Path.Combine(DataDir, "pins.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string PluginsDir => Path.Combine(DataDir, "Plugins");

    /// <summary>Copies of shortcuts imported from the old Linkbar-based version.</summary>
    public static string ImportedLinksDir => Path.Combine(DataDir, "ImportedLinks");

    /// <summary>Present while Facetbar has the Windows taskbar hidden; lets the next start undo a crash.</summary>
    public static string TaskbarHiddenMarker => Path.Combine(DataDir, "taskbar-hidden.json");

    /// <summary>Data folder of the previous Linkbar-based version.</summary>
    public static readonly string LegacyLinkbarDesktopsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkbarDesktops", "Desktops");

    static AppPaths() => Directory.CreateDirectory(DataDir);
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(AppPaths.LogFile);
                if (info.Exists && info.Length > 1024 * 1024) info.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the bar down.
            }
        }
    }

    public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex}");
}
