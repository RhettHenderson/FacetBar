using System.Runtime.InteropServices;
using System.Text;

namespace DeskBar.Core;

/// <summary>
/// Finds the program that really "is" an app when its windows belong to a helper process. Steam's windows
/// come from Steam\bin\cef\…\steamwebhelper.exe, started by Steam\steam.exe; pinning the window should pin
/// steam.exe, because launching the helper directly does nothing unless Steam is already running.
/// </summary>
internal static class ProcessTree
{
    /// <summary>
    /// The outermost ancestor process whose exe lives in a parent folder of this process's exe (e.g.
    /// steam.exe for steamwebhelper.exe), or null if the process isn't such a helper. Ancestors must still
    /// be running and have started before their child (guards against reused process ids).
    /// </summary>
    public static string? LauncherExe(uint pid)
    {
        var childExe = ExePath(pid);
        if (childExe == null) return null;
        var childStart = StartTime(pid);
        string? launcher = null;

        for (int depth = 0; depth < 6; depth++)
        {
            uint parent = ParentPid(pid);
            if (parent == 0 || parent == pid) break;
            var parentExe = ExePath(parent);
            var parentStart = StartTime(parent);
            if (parentExe == null || parentStart == null || childStart == null || parentStart > childStart) break;

            // Same exe (e.g. a helper spawning more helpers): keep climbing without recording it.
            if (!string.Equals(parentExe, childExe, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsStrictParentFolder(Path.GetDirectoryName(parentExe), Path.GetDirectoryName(childExe))) break;
                launcher = parentExe;
            }
            pid = parent;
            childStart = parentStart;
        }
        return launcher;
    }

    private static bool IsStrictParentFolder(string? parent, string? child) =>
        parent != null && child != null
        && child.Length > parent.Length + 1
        && child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string? ExePath(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static long? StartTime(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(process, out var created, out _, out _, out _) ? created : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint ParentPid(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return 0;
        try
        {
            var info = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(process, 0, ref info, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (uint)info.InheritedFromUniqueProcessId.ToInt64()
                : 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
}
