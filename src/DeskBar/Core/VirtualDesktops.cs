using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DeskBar.Core;

public sealed record DesktopInfo(Guid Id, int Index, string Name);

/// <summary>Tracks the current virtual desktop and raises events on the UI thread when it changes.</summary>
public sealed class VirtualDesktopService : IDisposable
{
    private readonly VirtualDesktopWatcher _watcher = new();
    private readonly Dispatcher _dispatcher;

    public IReadOnlyList<DesktopInfo> Desktops { get; private set; }
    public DesktopInfo Current { get; private set; }

    /// <summary>The user switched desktops (or the current one was renamed).</summary>
    public event EventHandler? CurrentChanged;
    /// <summary>Desktops were added, removed, reordered or renamed.</summary>
    public event EventHandler? DesktopsChanged;

    internal VirtualDesktopService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Desktops = VirtualDesktops.GetDesktops();
        Current = VirtualDesktops.GetCurrent(Desktops);
        _watcher.Changed += () => _dispatcher.BeginInvoke(Refresh);
        _watcher.Start();
    }

    private void Refresh()
    {
        var desktops = VirtualDesktops.GetDesktops();
        var current = VirtualDesktops.GetCurrent(desktops);

        bool listChanged = !desktops.SequenceEqual(Desktops);
        bool currentChanged = current != Current;
        Desktops = desktops;
        Current = current;

        if (listChanged) DesktopsChanged?.Invoke(this, EventArgs.Empty);
        if (currentChanged)
        {
            Log.Write($"Virtual desktop: {current.Name} ({current.Id})");
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose() => _watcher.Dispose();
}

/// <summary>
/// Reads virtual desktop state from the registry keys Explorer maintains. This avoids the
/// undocumented IVirtualDesktopManagerInternal COM interfaces, which change between Windows builds.
/// </summary>
internal static class VirtualDesktops
{
    public const string RootKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    // Windows 10 keeps the current desktop per logon session; Windows 11 keeps it in RootKey.
    public static readonly string SessionKey =
        $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{Process.GetCurrentProcess().SessionId}\VirtualDesktops";

    public static IReadOnlyList<DesktopInfo> GetDesktops()
    {
        var result = new List<DesktopInfo>();
        using var root = Registry.CurrentUser.OpenSubKey(RootKey);
        if (root?.GetValue("VirtualDesktopIDs") is byte[] ids)
        {
            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var id = new Guid(ids.AsSpan(i, 16));
                result.Add(new DesktopInfo(id, result.Count, ReadName(id) ?? $"Desktop {result.Count + 1}"));
            }
        }

        // Before a second desktop has ever been created, Explorer may not have written any IDs yet.
        if (result.Count == 0)
            result.Add(new DesktopInfo(Guid.Empty, 0, "Desktop 1"));

        return result;
    }

    public static DesktopInfo GetCurrent(IReadOnlyList<DesktopInfo> desktops)
    {
        var id = ReadGuid(SessionKey, "CurrentVirtualDesktop") ?? ReadGuid(RootKey, "CurrentVirtualDesktop");
        return desktops.FirstOrDefault(d => d.Id == id) ?? desktops[0];
    }

    private static string? ReadName(Guid id)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{RootKey}\Desktops\{id:B}");
        var name = key?.GetValue("Name") as string;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Guid? ReadGuid(string keyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is byte[] { Length: 16 } bytes ? new Guid(bytes) : null;
    }
}

/// <summary>
/// Raises <see cref="Changed"/> (on a background thread) whenever Explorer writes the
/// virtual desktop registry keys, plus once a second as a fallback.
/// </summary>
internal sealed class VirtualDesktopWatcher : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);

    public event Action? Changed;

    public VirtualDesktopWatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "VirtualDesktopWatcher" };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        // RegNotifyChangeKeyValue registrations belong to the calling thread, so all arming happens here.
        var watches = new[]
        {
            new KeyWatch(VirtualDesktops.SessionKey, watchSubtree: false),
            new KeyWatch(VirtualDesktops.RootKey, watchSubtree: true), // subtree catches desktop renames
        };
        var handles = watches.Select(w => (WaitHandle)w.Event).Append(_stop).ToArray();

        try
        {
            while (true)
            {
                foreach (var w in watches) w.EnsureArmed();

                int signaled = WaitHandle.WaitAny(handles, 1000);
                if (signaled == handles.Length - 1) return;
                if (signaled != WaitHandle.WaitTimeout) watches[signaled].Armed = false;

                Changed?.Invoke();
            }
        }
        finally
        {
            foreach (var w in watches) w.Dispose();
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(2000);
        _stop.Dispose();
    }

    private sealed class KeyWatch(string path, bool watchSubtree) : IDisposable
    {
        private RegistryKey? _key;
        public AutoResetEvent Event { get; } = new(false);
        public bool Armed { get; set; }

        public void EnsureArmed()
        {
            if (Armed) return;
            _key ??= Registry.CurrentUser.OpenSubKey(path); // may not exist yet; retried every loop
            if (_key == null) return;
            int rc = Native.RegNotifyChangeKeyValue(_key.Handle, watchSubtree,
                Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_CHANGE_NAME, Event.SafeWaitHandle, true);
            Armed = rc == 0;
        }

        public void Dispose()
        {
            _key?.Dispose();
            Event.Dispose();
        }
    }
}
