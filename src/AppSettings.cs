using System.Text.Json;
using Microsoft.Win32;

namespace LinkbarDesktops;

/// <summary>User settings stored in %APPDATA%\LinkbarDesktops\config.json.</summary>
internal sealed class AppSettings
{
    /// <summary>Full path to Linkbar.exe / Linkbar64.exe. Empty means "look next to LinkbarDesktops.exe".</summary>
    public string LinkbarPath { get; set; } = "";

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LinkbarDesktops");

    public static string ConfigPath => Path.Combine(DataDir, "config.json");
    public static string DesktopsDir => Path.Combine(DataDir, "Desktops");

    /// <summary>Style copied into bars for newly visited desktops (written by "Copy this bar's style to all desktops").</summary>
    public static string TemplatePath => Path.Combine(DataDir, "template.lbr");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(ConfigPath))
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new AppSettings();
            }
            catch (JsonException ex)
            {
                Log.Write($"config.json is invalid, using defaults: {ex.Message}");
            }
        }
        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));

    public string? ResolveLinkbarExe()
    {
        if (!string.IsNullOrWhiteSpace(LinkbarPath))
            return File.Exists(LinkbarPath) ? Path.GetFullPath(LinkbarPath) : null;

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "Linkbar", "Linkbar64.exe"),
            Path.Combine(baseDir, "Linkbar", "Linkbar.exe"),
            Path.Combine(baseDir, "Linkbar64.exe"),
            Path.Combine(baseDir, "Linkbar.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LinkbarDesktops";

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

internal static class Log
{
    private static readonly object Gate = new();
    private static string LogPath => Path.Combine(AppSettings.DataDir, "log.txt");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
