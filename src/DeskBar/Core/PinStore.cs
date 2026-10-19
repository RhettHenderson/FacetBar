using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;
using ManagedShell.ShellFolders;

namespace DeskBar.Core;

public enum PinScope { AllDesktops, ThisDesktop }

/// <summary>An app (or file/folder) pinned to the bar.</summary>
public sealed class PinnedApp
{
    /// <summary>Exe, .lnk, document or folder. Empty for packaged apps launched by AppUserModelId.</summary>
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// <summary>Packaged/explicit app id; used to launch Store apps and to match their windows.</summary>
    public string? AppUserModelId { get; set; }
    public string Name { get; set; } = "";

    private bool _resolved;
    private string? _targetExe;
    private string? _linkAppId;
    private ImageSource? _icon;

    /// <summary>Executable this pin starts, if any (resolved through .lnk files). Lower-case full path.</summary>
    [JsonIgnore]
    public string? TargetExe { get { Resolve(); return _targetExe; } }

    [JsonIgnore]
    public string? EffectiveAppId { get { Resolve(); return AppUserModelId ?? _linkAppId; } }

    /// <summary>True for Store/MSIX apps, which must be started by app id rather than by path.</summary>
    [JsonIgnore]
    public bool IsPackaged { get { Resolve(); return _isPackaged; } }

    [JsonIgnore]
    public ImageSource Icon => _icon ??= LoadIcon();

    private bool _isPackaged;

    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        string? target = Path;
        if (Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLink.Read(Path) is { } link)
        {
            target = link.TargetPath;
            _linkAppId = link.AppUserModelId;
        }
        if (target != null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            _targetExe = System.IO.Path.GetFullPath(target).ToLowerInvariant();

        // Pins pointing into WindowsApps break when the app updates (the folder name has the version in it).
        if (string.IsNullOrEmpty(Path) || PackagedApps.IsPackagedPath(target))
        {
            _isPackaged = true;
            if (AppUserModelId == null && _linkAppId == null && target != null)
                _linkAppId = PackagedApps.AppIdForPath(target);
        }
    }

    private ImageSource LoadIcon()
    {
        try
        {
            // Store apps: use the package's own logo. (ShellItem icons load asynchronously and come back
            // null on first access, which left closed Store-app pins as blank buttons.)
            if (IsPackaged && EffectiveAppId is { } appId)
                return PackagedApps.IconFor(appId) ?? IconImageConverter.GetDefaultIcon();
            return IconImageConverter.GetImageFromAssociatedIcon(Path, IconSize.Large) ?? IconImageConverter.GetDefaultIcon();
        }
        catch (Exception ex)
        {
            Log.Write($"No icon for pin {Name}: {ex.Message}");
            return IconImageConverter.GetDefaultIcon();
        }
    }

    public void Launch()
    {
        try
        {
            if (IsPackaged && EffectiveAppId is { } appId)
            {
                if (!ShellHelper.ActivateApplication(appId))
                    Process.Start(new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{appId}") { UseShellExecute = true });
                return;
            }
            var dir = System.IO.Path.GetDirectoryName(TargetExe ?? Path);
            Process.Start(new ProcessStartInfo(Path, Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = dir != null && Directory.Exists(dir) ? dir : "",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed a UAC / "broken shortcut" prompt.
        }
        catch (Exception ex)
        {
            Log.Error($"Launching {Name}", ex);
            System.Windows.MessageBox.Show($"Couldn't start {Name}:\n{ex.Message}", "DeskBar",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    public static PinnedApp FromPath(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) name = description;
        }
        else if (Directory.Exists(path))
        {
            name = new DirectoryInfo(path).Name;
        }
        return new PinnedApp { Path = path, Name = name };
    }
}

/// <summary>
/// Pinned apps: one list shown on every virtual desktop plus one list per desktop.
/// Stored in %APPDATA%\DeskBar\pins.json.
/// </summary>
public sealed class PinStore
{
    private sealed class Model
    {
        public List<PinnedApp> AllDesktops { get; set; } = [];
        public Dictionary<Guid, List<PinnedApp>> Desktops { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private Model _model;

    /// <summary>Raised on the UI thread whenever pins are added, removed or reordered.</summary>
    public event EventHandler? Changed;

    internal PinStore()
    {
        _model = Load();
    }

    /// <summary>Pins shown on the given desktop: the all-desktops pins first, then that desktop's own.</summary>
    public IReadOnlyList<PinnedApp> GetPins(Guid desktopId) =>
        _model.AllDesktops.Concat(DesktopList(desktopId, create: false) ?? []).ToList();

    public PinScope? ScopeOf(PinnedApp app, Guid desktopId)
    {
        if (_model.AllDesktops.Contains(app)) return PinScope.AllDesktops;
        if (DesktopList(desktopId, create: false)?.Contains(app) == true) return PinScope.ThisDesktop;
        return null;
    }

    public void Pin(PinnedApp app, PinScope scope, Guid desktopId)
    {
        var list = scope == PinScope.AllDesktops ? _model.AllDesktops : DesktopList(desktopId, create: true)!;
        list.Add(app);
        SaveAndNotify();
    }

    public void Unpin(PinnedApp app)
    {
        bool removed = _model.AllDesktops.Remove(app);
        foreach (var list in _model.Desktops.Values) removed |= list.Remove(app);
        if (removed) SaveAndNotify();
    }

    /// <summary>Moves a pin to a new position (an index into <see cref="GetPins"/>) within its own list.</summary>
    public void Move(PinnedApp app, int combinedIndex, Guid desktopId)
    {
        var list = _model.AllDesktops.Contains(app) ? _model.AllDesktops : DesktopList(desktopId, create: false);
        if (list == null || !list.Remove(app)) return;
        int offset = list == _model.AllDesktops ? 0 : _model.AllDesktops.Count;
        list.Insert(Math.Clamp(combinedIndex - offset, 0, list.Count), app);
        SaveAndNotify();
    }

    /// <summary>Moves a pin between "all desktops" and "this desktop".</summary>
    public void SetScope(PinnedApp app, PinScope scope, Guid desktopId)
    {
        if (ScopeOf(app, desktopId) == scope) return;
        Unpin(app);
        Pin(app, scope, desktopId);
    }

    private List<PinnedApp>? DesktopList(Guid desktopId, bool create)
    {
        if (!_model.Desktops.TryGetValue(desktopId, out var list) && create)
            _model.Desktops[desktopId] = list = [];
        return list;
    }

    private void SaveAndNotify()
    {
        try
        {
            File.WriteAllText(AppPaths.PinsFile, JsonSerializer.Serialize(_model, JsonOptions));
        }
        catch (IOException ex)
        {
            Log.Error("Saving pins.json", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Model Load()
    {
        if (File.Exists(AppPaths.PinsFile))
        {
            try
            {
                return JsonSerializer.Deserialize<Model>(File.ReadAllText(AppPaths.PinsFile), JsonOptions) ?? new Model();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Log.Error("Reading pins.json; starting empty", ex);
                return new Model();
            }
        }

        var model = ImportFromLinkbarVersion();
        File.WriteAllText(AppPaths.PinsFile, JsonSerializer.Serialize(model, JsonOptions));
        return model;
    }

    /// <summary>First run: carry over shortcuts from the previous Linkbar-per-desktop setup.</summary>
    private static Model ImportFromLinkbarVersion()
    {
        var model = new Model();
        if (!Directory.Exists(AppPaths.LegacyLinkbarDesktopsDir)) return model;

        foreach (var desktopDir in Directory.GetDirectories(AppPaths.LegacyLinkbarDesktopsDir))
        {
            var links = System.IO.Path.Combine(desktopDir, "links");
            if (!Guid.TryParse(System.IO.Path.GetFileName(desktopDir), out var desktopId) || !Directory.Exists(links))
                continue;

            var target = System.IO.Path.Combine(AppPaths.ImportedLinksDir, desktopId.ToString("D"));
            var pins = new List<PinnedApp>();
            // Linkbar keeps bookkeeping files (e.g. "list") next to the shortcuts; only take launchable items.
            string[] launchable = [".lnk", ".url", ".exe", ".appref-ms", ".bat", ".cmd"];
            foreach (var file in Directory.GetFiles(links).Order()
                         .Where(f => launchable.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
            {
                Directory.CreateDirectory(target);
                var copy = System.IO.Path.Combine(target, System.IO.Path.GetFileName(file));
                File.Copy(file, copy, overwrite: true);
                pins.Add(new PinnedApp { Path = copy, Name = System.IO.Path.GetFileNameWithoutExtension(file) });
            }
            if (pins.Count > 0)
            {
                model.Desktops[desktopId] = pins;
                Log.Write($"Imported {pins.Count} Linkbar shortcut(s) for desktop {desktopId}");
            }
        }
        return model;
    }
}
