using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;
using ManagedShell.Common.SupportingClasses;

namespace DeskBar.Core;

/// <summary>
/// Tells the bars when the Windows Start menu opens or closes (Win key, Start button), and can nudge the
/// Start menu so it doesn't cover a bar. Start lives in a z-order band above every normal window, so a bar
/// can't simply be drawn over it.
/// </summary>
internal sealed class StartMenuMonitor : IDisposable
{
    // Polled, not event-driven: ManagedShell's IAppVisibilityEvents declares its callbacks as returning
    // `long` without [PreserveSig], so .NET expects an extra out-pointer Windows never passes and crashes the
    // whole process (access violation in coreclr) the first time the Start menu opens. RetroBar polls too.
    private readonly AppVisibilityHelper _visibility = new(false);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public bool IsOpen { get; private set; }

    /// <summary>Raised on the UI thread when the Start menu opens or closes.</summary>
    public event EventHandler? Changed;

    public StartMenuMonitor()
    {
        _poll.Tick += (_, _) => Set(SafeIsLauncherVisible());
        _poll.Start();
    }

    private bool SafeIsLauncherVisible()
    {
        try { return _visibility.IsLauncherVisible(); }
        catch (COMException) { return IsOpen; }
    }

    private void Set(bool open)
    {
        if (open == IsOpen) return;
        IsOpen = open;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The Start menu's window while it's showing, or zero.</summary>
    public static IntPtr FindStartWindow() =>
        FindShellWindows().FirstOrDefault(w => w.IsStart).Handle;

    /// <summary>
    /// The showing Start and Search windows. They're immersive "CoreWindow"s, which EnumWindows skips, so
    /// they're found with FindWindowEx. Start's window covers its whole monitor (the menu is drawn inside it);
    /// Search's window is just the size of its panel.
    /// </summary>
    public static List<(IntPtr Handle, bool IsStart)> FindShellWindows()
    {
        var found = new List<(IntPtr, bool)>();
        var title = new StringBuilder(256);
        IntPtr hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Windows.UI.Core.CoreWindow", null)) != IntPtr.Zero)
        {
            if (!IsWindowVisible(hwnd)) continue;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string? process = null;
            try { process = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { }
            GetWindowText(hwnd, title, title.Capacity);
            // Start: StartMenuExperienceHost (Windows 10 1903+) or ShellExperienceHost (older builds).
            if (process is "StartMenuExperienceHost" || (process is "ShellExperienceHost" && title.ToString() == "Start"))
                found.Add((hwnd, true));
            else if (process is "SearchApp" or "SearchHost" or "SearchUI")
                found.Add((hwnd, false));
        }
        return found;
    }

    /// <summary>
    /// The screen edge the (hidden) Windows taskbar sits on. Start draws its menu next to that edge, so it
    /// only crowds DeskBar when both are on the same edge.
    /// </summary>
    public static BarEdge? ExplorerTaskbarEdge()
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var t)) return null;
        var monitor = System.Windows.Forms.Screen.FromHandle(tray).Bounds;
        int width = t.Right - t.Left, height = t.Bottom - t.Top;
        if (height >= width) return (t.Left + t.Right) / 2 > (monitor.Left + monitor.Right) / 2 ? BarEdge.Right : BarEdge.Left;
        return (t.Top + t.Bottom) / 2 > (monitor.Top + monitor.Bottom) / 2 ? BarEdge.Bottom : BarEdge.Top;
    }

    /// <summary>Moves a window to an absolute position (physical pixels). Returns false if Windows refused.</summary>
    public static bool MoveTo(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    /// <summary>The Start menu's visible bounds in physical pixels (without the invisible resize border).</summary>
    public static Native.RECT? GetBounds(IntPtr start)
    {
        if (DwmGetWindowAttribute(start, DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0)
            return r;
        return Native.GetWindowRect(start, out r) ? r : null;
    }

    /// <summary>
    /// Puts Start's window back at its monitor's corner, where Windows keeps it. Used by --restore-taskbar,
    /// in case DeskBar exited without undoing its nudge (Windows remembers the position).
    /// </summary>
    public static void ResetStartWindow()
    {
        var start = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Windows.UI.Core.CoreWindow", "Start");
        if (start == IntPtr.Zero) return;
        var monitor = System.Windows.Forms.Screen.FromHandle(start).Bounds;
        MoveTo(start, monitor.Left, monitor.Top);
    }

    /// <summary>Moves a window by an offset (physical pixels). Returns false if Windows refused.</summary>
    public static bool Move(IntPtr hwnd, int dx, int dy) =>
        Native.GetWindowRect(hwnd, out var r) && MoveTo(hwnd, r.Left + dx, r.Top + dy);

    public void Dispose()
    {
        _poll.Stop();
        _visibility.Dispose();
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Native.RECT value, int size);
}
