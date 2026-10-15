using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LinkbarDesktops;

internal static class Native
{
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_USER = 0x0400;

    // Linkbar registers its appbar with uCallbackMessage = WM_USER + 1 (LM_AB_CALLBACK in AccessBar.pas).
    public const uint LINKBAR_AB_CALLBACK = WM_USER + 1;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint ABM_NEW = 0x00;
    public const uint ABM_REMOVE = 0x01;
    public const int ABN_POSCHANGED = 0x01;

    public const int REG_NOTIFY_CHANGE_NAME = 0x1;
    public const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("shell32.dll")]
    public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool watchSubtree,
        int notifyFilter, SafeWaitHandle hEvent, bool asynchronous);

    public static void AppBarRegister(IntPtr hwnd)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uCallbackMessage = LINKBAR_AB_CALLBACK };
        SHAppBarMessage(ABM_NEW, ref data); // returns 0 if already registered, which is fine
    }

    public static void AppBarUnregister(IntPtr hwnd)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref data);
    }

    public static IntPtr FindWindowOfProcess(int processId, string className)
    {
        IntPtr found = IntPtr.Zero;
        var sb = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != processId) return true;
            sb.Clear();
            GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() != className) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
