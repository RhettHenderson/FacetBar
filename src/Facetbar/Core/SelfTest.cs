using System.Text;
using Facetbar.Widgets;

namespace Facetbar.Core;

/// <summary>
/// "Facetbar.exe --selftest": checks the non-visual plumbing (settings, virtual desktops, pins, shortcut
/// reading, Everything IPC) without opening a bar or touching the Windows taskbar. Writes selftest.txt.
/// </summary>
internal static class SelfTest
{
    public static string ReportPath => Path.Combine(AppPaths.DataDir, "selftest.txt");

    public static async Task<bool> RunAsync()
    {
        var report = new StringBuilder();
        bool ok = true;

        void Check(string name, Func<string> test)
        {
            try { report.AppendLine($"[ OK ] {name}: {test()}"); }
            catch (Exception ex) { ok = false; report.AppendLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        report.AppendLine($"Facetbar self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        // First, as in a normal start: settings must not be created before DeskBar's are copied over.
        Check("DeskBar migration", LegacyMigration.CopyDeskBarData);
        Check("Settings", () => $"edge={BarSettings.Current.Edge}, size={BarSettings.Current.SizePercent}%, " +
                                $"left=[{string.Join(",", BarSettings.Current.LeftWidgets)}], right=[{string.Join(",", BarSettings.Current.RightWidgets)}]");

        IReadOnlyList<DesktopInfo> desktops = [];
        Check("Virtual desktops", () =>
        {
            desktops = VirtualDesktops.GetDesktops();
            var current = VirtualDesktops.GetCurrent(desktops);
            return $"{desktops.Count} found ({string.Join(", ", desktops.Select(d => d.Name))}); current = {current.Name}";
        });

        Check("Pins", () =>
        {
            var store = new PinStore();
            static string Describe(PinnedApp p)
            {
                if (!p.IsPackaged) return $"{p.Name} -> {p.TargetExe ?? p.Path}";
                // Store apps must resolve to an app id or they can't be launched or matched to their windows.
                if (p.EffectiveAppId == null) throw new InvalidOperationException($"no app id found for Store app pin '{p.Name}'");
                if (PackagedApps.IconFor(p.EffectiveAppId) == null) throw new InvalidOperationException($"no icon for Store app pin '{p.Name}'");
                return $"{p.Name} -> Store app {p.EffectiveAppId} (icon OK)";
            }
            var lines = desktops.Select(d =>
            {
                var pins = store.GetPins(d.Id);
                return $"{d.Name}: {pins.Count}" + (pins.Count == 0 ? "" : $" [{string.Join("; ", pins.Select(Describe))}]");
            });
            return string.Join(" | ", lines);
        });

        Check("Shortcut reading", () =>
        {
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
            var links = Directory.EnumerateFiles(startMenu, "*.lnk", SearchOption.AllDirectories).Take(5).ToList();
            var resolved = links.Select(l => (l, info: ShellLink.Read(l))).ToList();
            if (resolved.All(r => r.info == null)) throw new InvalidOperationException("no shortcut could be read");
            return string.Join("; ", resolved.Select(r => $"{Path.GetFileNameWithoutExtension(r.l)} -> {r.info?.TargetPath ?? "?"}" +
                                                         (r.info?.AppUserModelId is { } id ? $" (id {id})" : "")));
        });

        Check("Widgets", () => string.Join(", ", WidgetRegistry.All.Select(w => w.Id)));

        Check("Helper processes", () =>
        {
            // Steam's windows belong to steamwebhelper.exe; pins should launch steam.exe instead.
            var helper = System.Diagnostics.Process.GetProcessesByName("steamwebhelper").FirstOrDefault();
            if (helper == null) return "skipped (Steam isn't running)";
            var launcher = ProcessTree.LauncherExe((uint)helper.Id);
            if (launcher == null || !launcher.EndsWith(@"\steam.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"steamwebhelper.exe traced to {launcher ?? "nothing"}, expected steam.exe");
            return $"steamwebhelper.exe -> {launcher}";
        });

        Check("Start menu detection", () =>
        {
            // Same "is Start open?" query the bars poll (no COM events; see StartMenuMonitor).
            using var visibility = new ManagedShell.Common.Helpers.AppVisibilityHelper(false);
            bool open = visibility.IsLauncherVisible();
            var start = StartMenuMonitor.FindStartWindow();
            // The self-test runs from a command line, so Start should be closed (its window cloaked).
            return $"launcher visible = {open}; " +
                   (start == IntPtr.Zero ? "Start window correctly not found while closed" : $"found window {start:X} (Start open?)");
        });

        Check("Renamed exe matching", () =>
        {
            // A running app whose exe was renamed (app.exe -> app.prev.exe) must still match its pin.
            var dir = Directory.CreateTempSubdirectory("facetbar-selftest").FullName;
            try
            {
                var renamed = Path.Combine(dir, "notepad.prev.exe");
                File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), renamed);
                if (!Widgets.Tasks.TaskGroup.IsRenamedCopy(Path.Combine(dir, "notepad.exe"), renamed))
                    throw new InvalidOperationException("notepad.prev.exe wasn't matched to a notepad.exe pin");
                if (Widgets.Tasks.TaskGroup.IsRenamedCopy(Path.Combine(dir, "calc.exe"), renamed))
                    throw new InvalidOperationException("notepad.prev.exe was wrongly matched to a calc.exe pin");
                return "notepad.prev.exe matches a notepad.exe pin, not a calc.exe pin";
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        });

        try
        {
            if (!EverythingClient.IsRunning)
            {
                report.AppendLine("[WARN] Everything: not running, search box will offer to start it");
            }
            else
            {
                using var client = new EverythingClient();
                var results = await client.QueryAsync("notepad.exe", 5);
                if (results == null) { ok = false; report.AppendLine("[FAIL] Everything: no reply"); }
                else report.AppendLine($"[ OK ] Everything: {results.Count} result(s), e.g. {results.FirstOrDefault()?.FullPath}");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            report.AppendLine($"[FAIL] Everything: {ex.Message}");
        }

        report.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        File.WriteAllText(ReportPath, report.ToString());
        return ok;
    }
}
