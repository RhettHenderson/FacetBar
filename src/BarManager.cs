namespace LinkbarDesktops;

/// <summary>
/// Keeps one Linkbar instance per virtual desktop and makes sure only the current desktop's bar
/// is visible and reserving screen space. All members must be used from the UI thread.
/// </summary>
internal sealed class BarManager(string linkbarExe)
{
    private readonly Dictionary<Guid, BarInstance> _bars = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // the async operations below must not interleave
    private Guid? _lastCurrentId;
    private bool _syncing;
    private bool _syncAgain;
    private bool _shutDown;

    public IReadOnlyList<DesktopInfo> Desktops { get; private set; } = [];
    public DesktopInfo? Current { get; private set; }

    public event Action? CurrentChanged;

    public static string FolderFor(Guid desktopId) => Path.Combine(AppSettings.DesktopsDir, desktopId.ToString("D"));
    public static string LbrPathFor(Guid desktopId) => Path.Combine(FolderFor(desktopId), "bar.lbr");
    public static string LinksDirFor(Guid desktopId) => Path.Combine(FolderFor(desktopId), "links");

    /// <summary>Starts bars for every desktop that already has one so later switches are instant.</summary>
    public Task StartAsync() => LockedAsync(StartCoreAsync);

    private async Task StartCoreAsync()
    {
        Desktops = VirtualDesktops.GetDesktops();
        var current = VirtualDesktops.GetCurrent(Desktops);
        foreach (var desktop in Desktops.Where(d => d.Id != current.Id && File.Exists(LbrPathFor(d.Id))))
        {
            var bar = await EnsureRunningAsync(desktop);
            bar?.Hide(); // hide right away so bars don't stack up on screen during startup
        }
        RequestSync();
    }

    /// <summary>Safe to call any number of times from any UI event; runs are serialized and coalesced.</summary>
    public async void RequestSync()
    {
        if (_syncing)
        {
            _syncAgain = true;
            return;
        }
        _syncing = true;
        try
        {
            do
            {
                _syncAgain = false;
                await LockedAsync(SyncAsync);
            } while (_syncAgain);
        }
        catch (Exception ex)
        {
            Log.Write($"Sync failed: {ex}");
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task SyncAsync()
    {
        if (_shutDown) return;
        var desktops = VirtualDesktops.GetDesktops();
        var current = VirtualDesktops.GetCurrent(desktops);
        Desktops = desktops;

        // Desktop was removed in Task View: close its bar but keep its folder.
        foreach (var gone in _bars.Keys.Where(id => desktops.All(d => d.Id != id)).ToList())
        {
            Log.Write($"Desktop {gone} no longer exists; closing its bar");
            await _bars[gone].CloseAsync();
            _bars.Remove(gone);
        }

        bool switched = current.Id != _lastCurrentId;
        BarInstance? currentBar = _bars.GetValueOrDefault(current.Id);

        // Only (re)start on a switch, so closing a bar from its own menu keeps it closed until you come back.
        if (switched)
        {
            Log.Write($"Current desktop: {current.Name} ({current.Id})");
            var previousBar = _lastCurrentId is { } prev ? _bars.GetValueOrDefault(prev) : null;
            currentBar = await EnsureRunningAsync(current, previousBar?.LbrPath);
            _lastCurrentId = current.Id;
        }

        // Hide first, then show, so two bars never reserve space at the same time.
        foreach (var bar in _bars.Values.Where(b => b != currentBar))
            bar.Hide();
        currentBar?.Show();

        if (switched || Current?.Name != current.Name)
        {
            Current = current;
            CurrentChanged?.Invoke();
        }
    }

    private async Task<BarInstance?> EnsureRunningAsync(DesktopInfo desktop, string? styleSourceLbr = null)
    {
        if (_bars.TryGetValue(desktop.Id, out var existing) && existing.IsAlive)
            return existing;
        _bars.Remove(desktop.Id);

        var lbr = LbrPathFor(desktop.Id);
        if (!File.Exists(lbr) || !Directory.Exists(LinksDirFor(desktop.Id)))
            CreateBarFiles(desktop, styleSourceLbr);
        WriteNameHint(desktop);

        var bar = new BarInstance(desktop.Id, lbr);
        if (!await bar.StartAsync(linkbarExe))
            return null;
        _bars[desktop.Id] = bar;
        return bar;
    }

    private static void CreateBarFiles(DesktopInfo desktop, string? styleSourceLbr)
    {
        var links = LinksDirFor(desktop.Id);
        Directory.CreateDirectory(links);

        // New desktops copy the saved template, or else the bar you just came from, so all bars match.
        var source = File.Exists(AppSettings.TemplatePath) ? AppSettings.TemplatePath : styleSourceLbr;
        var settings = source != null && File.Exists(source)
            ? LbrFile.ReadStyle(source)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        settings[LbrFile.LinksDirKey] = links;

        var lbr = LbrPathFor(desktop.Id);
        LbrFile.WriteMain(lbr, settings);
        Log.Write($"Created bar for {desktop.Name} at {lbr} (style from {source ?? "Linkbar defaults"})");
    }

    // The folder is named by GUID so renaming a desktop doesn't lose its bar; this file says which one it is.
    private static void WriteNameHint(DesktopInfo desktop)
    {
        var hint = Path.Combine(FolderFor(desktop.Id), "desktop-name.txt");
        if (!File.Exists(hint) || File.ReadAllText(hint) != desktop.Name)
            File.WriteAllText(hint, desktop.Name);
    }

    /// <summary>Copies the current bar's look/position to every other bar and the template, then restarts those bars.</summary>
    public Task CopyCurrentStyleToAllAsync() => LockedAsync(async () =>
    {
        if (Current == null) return;
        var style = LbrFile.ReadStyle(LbrPathFor(Current.Id));
        LbrFile.WriteMain(AppSettings.TemplatePath, style);

        foreach (var desktop in Desktops.Where(d => d.Id != Current.Id && File.Exists(LbrPathFor(d.Id))))
        {
            if (_bars.Remove(desktop.Id, out var bar))
                await bar.CloseAsync(); // close first: Linkbar rewrites its .lbr when it saves settings

            var merged = new Dictionary<string, string>(style, StringComparer.OrdinalIgnoreCase)
            {
                [LbrFile.LinksDirKey] = LinksDirFor(desktop.Id),
            };
            LbrFile.WriteMain(LbrPathFor(desktop.Id), merged);

            (await EnsureRunningAsync(desktop))?.Hide();
        }
        RequestSync();
    });

    public Task RestartAllAsync() => LockedAsync(async () =>
    {
        await CloseAllCoreAsync();
        _lastCurrentId = null;
        await StartCoreAsync();
    });

    /// <summary>Closes every bar and stops reacting to desktop changes (used on exit).</summary>
    public Task ShutdownAsync() => LockedAsync(() =>
    {
        _shutDown = true;
        return CloseAllCoreAsync();
    });

    /// <summary>Explorer restarted: Linkbar re-registered every bar, so re-apply which one is visible.</summary>
    public void OnTaskbarRecreated()
    {
        foreach (var bar in _bars.Values)
            bar.ForgetState();
        RequestSync();
    }

    private async Task CloseAllCoreAsync()
    {
        foreach (var bar in _bars.Values)
            await bar.CloseAsync();
        _bars.Clear();
    }

    private async Task LockedAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
