using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Paladin.Core.Logging;

namespace Paladin.Launcher.Platform;

/// <summary>
/// Puts the game's window in front once it exists, and once more if it is pushed
/// down while it is still starting.
///
/// When Age of Empires IV goes fullscreen a few seconds after launch, anything
/// that holds the foreground at that instant — Steam starting up, a screen
/// recorder's toolbar, the browser the paladin:// link came from — wins, and the
/// game drops to the taskbar behind a black screen. The user then has to find it
/// with Alt+Tab. This watches the new process for its main window, brings it to
/// the front when it appears, and for a short guard period brings it back if it
/// gets minimised. It never fights a deliberate Alt+Tab: only a minimised window
/// is raised again, and only within the guard window, a bounded number of times.
///
/// Windows refuses SetForegroundWindow from a process that does not own the
/// foreground. The accepted way round that is what a user does by hand: a
/// synthetic Alt press unlocks the rule for the next call.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameWindow
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>The largest visible top-level window owned by the process, or zero.</summary>
    public static IntPtr FindMainWindow(int pid)
    {
        var best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var owner);
            if (owner != (uint)pid || !IsWindowVisible(hWnd)) return true;
            long area = 0;
            if (GetWindowRect(hWnd, out var r)) area = Math.Max(0L, r.Right - r.Left) * Math.Max(0L, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hWnd; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Restore if minimised and make foreground. True when the window ended up in front.</summary>
    public static bool BringToFront(IntPtr hWnd, PaladinLog log)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        if (SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd) return true;

        // Not the foreground process: press and release Alt, then ask again.
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        var accepted = SetForegroundWindow(hWnd);
        var inFront = accepted && GetForegroundWindow() == hWnd;
        if (!inFront) log.Warn($"SetForegroundWindow was refused for window {hWnd} (accepted={accepted}).");
        return inFront;
    }

    /// <summary>
    /// Wait up to <paramref name="appearTimeout"/> for the process's window, bring it
    /// to the front, then for <paramref name="guard"/> bring it back whenever it is
    /// found minimised, at most <paramref name="maxRaises"/> times in all. Returns the
    /// number of times the window was put in front.
    /// </summary>
    public static async Task<int> KeepInFrontAsync(
        int pid, TimeSpan appearTimeout, TimeSpan guard, int maxRaises, PaladinLog log, Action<string>? onRaised, CancellationToken ct)
    {
        var appearBy = DateTime.UtcNow + appearTimeout;
        var hWnd = IntPtr.Zero;
        while (DateTime.UtcNow < appearBy && !ct.IsCancellationRequested)
        {
            hWnd = FindMainWindow(pid);
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(500, ct);
        }
        if (hWnd == IntPtr.Zero)
        {
            log.Warn($"No window appeared for pid {pid} within {appearTimeout.TotalSeconds:0}s; nothing to bring to the front.");
            return 0;
        }

        var raises = 0;
        if (BringToFront(hWnd, log))
        {
            raises++;
            log.Info($"Brought window {hWnd} of pid {pid} to the front.");
            onRaised?.Invoke("Brought Age of Empires IV to the front");
        }

        var guardUntil = DateTime.UtcNow + guard;
        while (DateTime.UtcNow < guardUntil && raises < maxRaises && !ct.IsCancellationRequested)
        {
            await Task.Delay(1000, ct);
            // The game may swap its splash window for the real one; follow the largest visible one.
            var current = FindMainWindow(pid);
            if (current == IntPtr.Zero) continue;
            var swapped = current != hWnd;
            hWnd = current;
            if (swapped || IsIconic(hWnd))
            {
                if (BringToFront(hWnd, log))
                {
                    raises++;
                    log.Info($"Brought window {hWnd} of pid {pid} back to the front ({(swapped ? "new window" : "was minimised")}).");
                    onRaised?.Invoke("Brought Age of Empires IV back to the front");
                }
            }
        }
        return raises;
    }
}
