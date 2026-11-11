using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace Facetbar.Core;

/// <summary>
/// Facetbar was called DeskBar until 3.0. When Facetbar starts, it closes a running DeskBar, and the first
/// time it starts it carries over DeskBar's settings, pins and "start with Windows" entry.
/// </summary>
internal static class LegacyMigration
{
    private const string DeskBarExitEvent = @"Local\DeskBar.Exit";
    private const string DeskBarRunValue = "DeskBar";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly string DeskBarDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskBar");

    public static void Run()
    {
        try
        {
            StopRunningDeskBar();
            CopyDeskBarData();
            MoveStartupEntry();
        }
        catch (Exception ex)
        {
            Log.Error("Migrating from DeskBar", ex); // never block startup over this
        }
    }

    /// <summary>DeskBar would fight Facetbar for the screen edge and the Windows taskbar; ask it to quit.</summary>
    private static void StopRunningDeskBar()
    {
        if (!EventWaitHandle.TryOpenExisting(DeskBarExitEvent, out var exit)) return;
        using (exit) exit.Set();
        Log.Write("Asked the running DeskBar to exit");
        foreach (var process in Process.GetProcessesByName("DeskBar"))
        {
            using (process)
            {
                try { process.WaitForExit(8000); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
    }

    /// <summary>
    /// First start only (no Facetbar settings yet): copy %APPDATA%\DeskBar into %APPDATA%\Facetbar.
    /// Returns what happened, for the self-test.
    /// </summary>
    internal static string CopyDeskBarData()
    {
        if (!Directory.Exists(DeskBarDataDir)) return "no DeskBar data to copy";
        if (File.Exists(AppPaths.SettingsFile)) return "Facetbar already has its own settings; nothing copied";

        foreach (var file in Directory.GetFiles(DeskBarDataDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(AppPaths.DataDir, Path.GetRelativePath(DeskBarDataDir, file));
            if (File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        // Pins to shortcuts imported from the Linkbar version point into the old folder; point them at the copies.
        if (File.Exists(AppPaths.PinsFile))
        {
            static string JsonEscaped(string path) => JsonSerializer.Serialize(path)[1..^1];
            var json = File.ReadAllText(AppPaths.PinsFile);
            var updated = json.Replace(JsonEscaped(DeskBarDataDir + "\\"), JsonEscaped(AppPaths.DataDir + "\\"), StringComparison.OrdinalIgnoreCase);
            if (updated != json) File.WriteAllText(AppPaths.PinsFile, updated);
        }

        Log.Write($"Copied DeskBar's settings and pins from {DeskBarDataDir}");
        return $"copied DeskBar's settings and pins from {DeskBarDataDir}";
    }

    /// <summary>Replace DeskBar's "start with Windows" entry with Facetbar's.</summary>
    private static void MoveStartupEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(DeskBarRunValue) == null) return;
        key.DeleteValue(DeskBarRunValue, throwOnMissingValue: false);
        BarSettings.StartWithWindows = true;
        Log.Write("Moved the start-with-Windows entry from DeskBar to Facetbar");
    }
}
