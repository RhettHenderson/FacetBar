using System.Text.Json;
using ManagedShell.AppBar;
using ManagedShell.Interop;

namespace DeskBar.Core;

/// <summary>
/// Makes sure the Windows taskbar comes back. While DeskBar hides it, a marker file records the taskbar's
/// original state; if DeskBar crashes or is killed, the next start (or "DeskBar.exe --restore-taskbar")
/// restores it from that marker.
/// </summary>
internal static class TaskbarGuard
{
    private sealed record Marker(int OriginalState);

    private const int SWP_SHOWWINDOW = 0x0040;

    public static void RecoverIfNeeded(ExplorerHelper explorer)
    {
        if (!File.Exists(AppPaths.TaskbarHiddenMarker)) return;
        try
        {
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(AppPaths.TaskbarHiddenMarker));
            Log.Write($"Restoring the Windows taskbar left hidden by a previous run (state {marker?.OriginalState})");
            explorer.SetTaskbarState((NativeMethods.ABState)(marker?.OriginalState ?? (int)NativeMethods.ABState.Default));
            explorer.SetTaskbarVisibility(SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Log.Error("Restoring Windows taskbar", ex);
        }
        File.Delete(AppPaths.TaskbarHiddenMarker);
    }

    public static void Hide(ExplorerHelper explorer)
    {
        if (explorer.HideExplorerTaskbar) return;
        var state = (int)explorer.GetTaskbarState();
        File.WriteAllText(AppPaths.TaskbarHiddenMarker, JsonSerializer.Serialize(new Marker(state)));
        explorer.HideExplorerTaskbar = true;
    }

    public static void Show(ExplorerHelper explorer)
    {
        if (!explorer.HideExplorerTaskbar) return;
        explorer.HideExplorerTaskbar = false; // ManagedShell restores the state it saw before hiding
        File.Delete(AppPaths.TaskbarHiddenMarker);
    }
}
