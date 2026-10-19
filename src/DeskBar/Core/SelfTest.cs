using System.Text;
using DeskBar.Widgets;

namespace DeskBar.Core;

/// <summary>
/// "DeskBar.exe --selftest": checks the non-visual plumbing (settings, virtual desktops, pins, shortcut
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

        report.AppendLine($"DeskBar self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
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
            var lines = desktops.Select(d =>
            {
                var pins = store.GetPins(d.Id);
                return $"{d.Name}: {pins.Count}" + (pins.Count == 0 ? "" :
                    $" [{string.Join("; ", pins.Select(p => $"{p.Name} -> {p.TargetExe ?? p.EffectiveAppId ?? p.Path}"))}]");
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
