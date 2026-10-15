using System.Diagnostics;

namespace LinkbarDesktops;

/// <summary>One running Linkbar process, bound to one virtual desktop.</summary>
internal sealed class BarInstance(Guid desktopId, string lbrPath)
{
    private const string LinkbarWindowClass = "TLinkbarWcl";

    private Process? _process;
    private IntPtr _hwnd;
    private bool? _shown; // null = unknown, re-apply on next Show/Hide

    public Guid DesktopId { get; } = desktopId;
    public string LbrPath { get; } = lbrPath;
    public IntPtr Hwnd => _hwnd;

    public bool IsAlive => _process is { HasExited: false } && Native.IsWindow(_hwnd);

    public async Task<bool> StartAsync(string linkbarExe)
    {
        // Same switch format Linkbar uses when it launches its own bars (LBCreateCommandParam).
        var arg = LbrPath.Contains(' ') ? $"\"{LbrPath}\"" : LbrPath;
        _process = Process.Start(new ProcessStartInfo(linkbarExe, $"/f{arg}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(linkbarExe)!,
        });
        if (_process == null) return false;

        // Don't touch the bar until Linkbar has finished initializing: if we register its window as an
        // appbar before Linkbar does, Linkbar's own ABM_NEW fails and it shows an error dialog.
        var process = _process;
        await Task.Run(() =>
        {
            try { process.WaitForInputIdle(10_000); }
            catch (InvalidOperationException) { } // exited already; handled below
        });

        for (int i = 0; i < 200; i++) // up to ~10s
        {
            if (_process.HasExited)
            {
                Log.Write($"Linkbar exited during startup (code {_process.ExitCode}) for {LbrPath}");
                return false;
            }
            _hwnd = Native.FindWindowOfProcess(_process.Id, LinkbarWindowClass);
            if (_hwnd != IntPtr.Zero && Native.IsWindowVisible(_hwnd)) break;
            _hwnd = IntPtr.Zero;
            await Task.Delay(50);
        }

        if (_hwnd == IntPtr.Zero)
        {
            Log.Write($"Linkbar window never appeared for {LbrPath}; killing pid {_process.Id}");
            TryKill();
            return false;
        }

        _shown = null;
        Log.Write($"Started Linkbar pid {_process.Id} hwnd 0x{_hwnd:X} for {LbrPath}");
        return true;
    }

    // Autohide bars don't reserve screen space (Linkbar unregisters them), so leave their appbar state alone.
    private bool ReservesSpace => !LbrFile.ReadBool(LbrPath, LbrFile.AutoHideKey);

    public void Show()
    {
        if (!IsAlive) return;
        if (_shown == true && Native.IsWindowVisible(_hwnd)) return;

        bool reserve = ReservesSpace;
        if (reserve) Native.AppBarRegister(_hwnd);
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        // Ask Linkbar to run its own ABM_QUERYPOS/ABM_SETPOS logic so it claims its strip of the screen again.
        if (reserve) Native.PostMessage(_hwnd, Native.LINKBAR_AB_CALLBACK, Native.ABN_POSCHANGED, IntPtr.Zero);
        _shown = true;
        Log.Write($"Shown bar 0x{_hwnd:X} (desktop {DesktopId})");
    }

    public void Hide()
    {
        if (!IsAlive) return;
        if (_shown == false && !Native.IsWindowVisible(_hwnd)) return;

        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        // Release the reserved screen space; otherwise every hidden bar would still push windows aside.
        if (ReservesSpace) Native.AppBarUnregister(_hwnd);
        _shown = false;
        Log.Write($"Hid bar 0x{_hwnd:X} (desktop {DesktopId})");
    }

    /// <summary>Call after Explorer restarts: Linkbar re-registers every bar, so our cached state is stale.</summary>
    public void ForgetState() => _shown = null;

    public async Task CloseAsync()
    {
        if (_process is not { HasExited: false }) return;

        // Linkbar's ABM_REMOVE on exit is harmless for a hidden (already unregistered) bar.
        Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Write($"Linkbar pid {_process.Id} didn't close in time; killing it");
            TryKill();
        }
    }

    private void TryKill()
    {
        try { _process?.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
