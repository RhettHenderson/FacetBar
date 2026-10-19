using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using DeskBar.Core;

namespace DeskBar.Widgets;

public sealed record EverythingResult(string Name, string Directory, bool IsFolder)
{
    public string FullPath => System.IO.Path.Combine(Directory, Name);
}

/// <summary>
/// Talks to a running Everything (voidtools) instance over its WM_COPYDATA IPC interface
/// (EVERYTHING_IPC_COPYDATA_QUERY2W), so no SDK DLL is needed. Must be used on the UI thread.
/// </summary>
internal sealed class EverythingClient : IDisposable
{
    private const string IpcWindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";
    private const int CopyDataQuery2W = 18;
    private const uint RequestName = 0x1, RequestPath = 0x2;
    private const uint SortNameAscending = 1;
    private const uint FlagFolder = 0x1;

    private readonly HwndSource _replyWindow;
    private uint _lastQueryId = 0x4442_0000;
    private TaskCompletionSource<IReadOnlyList<EverythingResult>>? _pending;
    private uint _pendingId;

    public EverythingClient()
    {
        // Message-only window that receives Everything's replies.
        _replyWindow = new HwndSource(new HwndSourceParameters("DeskBarEverythingReply") { ParentWindow = new IntPtr(-3) });
        _replyWindow.AddHook(WndProc);
    }

    public static bool IsRunning => Native.FindWindow(IpcWindowClass, null) != IntPtr.Zero;

    /// <summary>Runs a search. Returns null if Everything isn't running or didn't answer in time.</summary>
    public async Task<IReadOnlyList<EverythingResult>?> QueryAsync(string search, int maxResults)
    {
        var everything = Native.FindWindow(IpcWindowClass, null);
        if (everything == IntPtr.Zero) return null;

        _pending?.TrySetCanceled(); // only the newest query matters
        var tcs = new TaskCompletionSource<IReadOnlyList<EverythingResult>>();
        _pending = tcs;
        _pendingId = ++_lastQueryId;

        // EVERYTHING_IPC_QUERY2: seven DWORDs followed by the null-terminated UTF-16 search string.
        var text = Encoding.Unicode.GetBytes(search + "\0");
        var buffer = new byte[7 * 4 + text.Length];
        BitConverter.GetBytes((uint)_replyWindow.Handle.ToInt64()).CopyTo(buffer, 0); // reply_hwnd
        BitConverter.GetBytes(_pendingId).CopyTo(buffer, 4);                           // reply_copydata_message
        BitConverter.GetBytes(0u).CopyTo(buffer, 8);                                   // search_flags
        BitConverter.GetBytes(0u).CopyTo(buffer, 12);                                  // offset
        BitConverter.GetBytes((uint)maxResults).CopyTo(buffer, 16);                    // max_results
        BitConverter.GetBytes(RequestName | RequestPath).CopyTo(buffer, 20);           // request_flags
        BitConverter.GetBytes(SortNameAscending).CopyTo(buffer, 24);                   // sort_type
        text.CopyTo(buffer, 28);

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var cds = new Native.COPYDATASTRUCT { dwData = CopyDataQuery2W, cbData = buffer.Length, lpData = handle.AddrOfPinnedObject() };
            if (Native.SendMessage(everything, Native.WM_COPYDATA, _replyWindow.Handle, ref cds) == IntPtr.Zero)
                return null;
        }
        finally
        {
            handle.Free();
        }

        var finished = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        if (finished != tcs.Task || tcs.Task.IsCanceled) return null;
        return tcs.Task.Result;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_COPYDATA) return IntPtr.Zero;
        var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
        if ((uint)cds.dwData.ToInt64() != _pendingId || _pending == null) return IntPtr.Zero;

        handled = true;
        var data = new byte[cds.cbData];
        Marshal.Copy(cds.lpData, data, 0, cds.cbData);
        _pending.TrySetResult(Parse(data));
        return new IntPtr(1);
    }

    /// <summary>EVERYTHING_IPC_LIST2: header of five DWORDs, then {flags, data_offset} per item; each string is a DWORD length + UTF-16 text + null.</summary>
    private static List<EverythingResult> Parse(byte[] data)
    {
        var results = new List<EverythingResult>();
        uint count = BitConverter.ToUInt32(data, 4);
        for (int i = 0; i < count; i++)
        {
            int itemOffset = 20 + i * 8;
            uint flags = BitConverter.ToUInt32(data, itemOffset);
            int p = (int)BitConverter.ToUInt32(data, itemOffset + 4);
            string name = ReadString(data, ref p);
            string path = ReadString(data, ref p);
            results.Add(new EverythingResult(name, path, (flags & FlagFolder) != 0));
        }
        return results;
    }

    private static string ReadString(byte[] data, ref int p)
    {
        int length = (int)BitConverter.ToUInt32(data, p);
        var s = Encoding.Unicode.GetString(data, p + 4, length * 2);
        p += 4 + (length + 1) * 2;
        return s;
    }

    /// <summary>Opens Everything's own window with the given search.</summary>
    public static void OpenInEverything(string search)
    {
        var exe = FindEverythingExe();
        if (exe == null)
        {
            Log.Write("Everything.exe not found");
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "-search", search }, UseShellExecute = false });
    }

    public static void StartEverything()
    {
        var exe = FindEverythingExe();
        if (exe != null) Process.Start(new ProcessStartInfo(exe, "-startup") { UseShellExecute = false });
    }

    private static string? FindEverythingExe()
    {
        var configured = BarSettings.Current.EverythingPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

        foreach (var process in Process.GetProcessesByName("Everything"))
        {
            try { return process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { } // elevated instance
            finally { process.Dispose(); }
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "Everything.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "Everything.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        _pending?.TrySetCanceled();
        _replyWindow.Dispose();
    }
}
