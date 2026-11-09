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
    public static IntPtr FindStartWindow()
    {
        IntPtr found = IntPtr.Zero;
        var cls = new StringBuilder(256);
        var title = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() != "Windows.UI.Core.CoreWindow") return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            // Hosted by StartMenuExperienceHost (Windows 10 1903+) or ShellExperienceHost (older builds).
            GetWindowThreadProcessId(hwnd, out uint pid);
            string? process = null;
            try { process = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { }
            GetWindowText(hwnd, title, title.Capacity);
            bool isStart = process is "StartMenuExperienceHost"
                           || (process is "ShellExperienceHost" && title.ToString() == "Start");
            if (!isStart) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The Start menu's visible bounds in physical pixels (without the invisible resize border).</summary>
    public static Native.RECT? GetBounds(IntPtr start)
    {
        if (DwmGetWindowAttribute(start, DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0)
            return r;
        return Native.GetWindowRect(start, out r) ? r : null;
    }

    /// <summary>Moves the Start menu by the given offset (physical pixels). Returns false if Windows refused.</summary>
    public static bool Move(IntPtr start, int dx, int dy)
    {
        if (!Native.GetWindowRect(start, out var r)) return false;
        return SetWindowPos(start, IntPtr.Zero, r.Left + dx, r.Top + dy, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

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
