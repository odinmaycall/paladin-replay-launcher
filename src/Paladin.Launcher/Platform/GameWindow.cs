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
/// foreground. There are three ways round that and this asks for them in order of
/// how little they disturb the machine: plainly, then by borrowing the foreground
/// thread's input queue, and only then with the synthetic Alt press that unlocks the
/// rule the way a user's own Alt does.
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
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>
    /// How many times, and how far apart, the ladder re-reads the foreground before it
    /// climbs a rung. At most 150 ms, and only on the path that would otherwise press a key.
    /// </summary>
    private const int SettleChecks = 3;
    private const int SettleDelayMs = 50;

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

    /// <summary>
    /// Restore if minimised and make foreground. True when the window ended up in front.
    ///
    /// Three ways are tried, politest first, and the log names the one that worked so a
    /// live run can be read afterwards:
    ///
    ///   1. SetForegroundWindow on its own. It succeeds whenever Windows has no reason to
    ///      refuse — most often because the launcher's own console still owns the
    ///      foreground when the game's window appears.
    ///   2. AttachThreadInput to the thread that owns the current foreground window, ask
    ///      again, detach in a finally. Sharing that thread's input queue makes this
    ///      process one Windows will take the call from, and it presses nothing.
    ///   3. The synthetic Alt press, last. It is the documented trick and it works, but it
    ///      is a real key event: with the game's own accessibility option
    ///      `uielementnarration = true` an Alt on the loading screen moves UI focus and the
    ///      game reads the interface ALOUD. Cosmetic, startling, and it happened on the
    ///      owner's own live runs, where step 1 was refused and the Alt was all that was
    ///      left. Step 2 is the new one, and it presses nothing. Nothing here reads or
    ///      writes that option or any other game setting — the fix is for the launcher to
    ///      reach for the keyboard less often, never for the user to change their game.
    ///
    /// Between the rungs the foreground is read again for up to 150 ms
    /// (<see cref="SettledInFront"/>), because two common cases look like a refusal from
    /// here and are not: the window is already in front (AoE4 raises itself as it goes
    /// fullscreen — the 0.3.0 note — which is the same second this runs), and a
    /// cross-process SetForegroundWindow that was accepted but whose activation is still
    /// on its way to the target thread. Climbing to a keystroke on either of those is the
    /// exact way defect 1 came back.
    /// </summary>
    public static bool BringToFront(IntPtr hWnd, PaladinLog log)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return false;
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);

        if (TrySetForeground(hWnd))
        {
            log.Info($"Window {hWnd} came to the front on the plain SetForegroundWindow (no keys pressed).");
            return true;
        }

        if (SettledInFront(hWnd))
        {
            log.Info($"Window {hWnd} is in front after the plain SetForegroundWindow settled (no keys pressed).");
            return true;
        }

        if (TryAttachedSetForeground(hWnd, log))
        {
            log.Info($"Window {hWnd} came to the front after attaching to the foreground thread's input (no keys pressed).");
            return true;
        }

        if (SettledInFront(hWnd))
        {
            log.Info($"Window {hWnd} is in front after the attached SetForegroundWindow settled (no keys pressed).");
            return true;
        }

        // Last resort: press and release Alt, then ask again.
        log.Info($"Window {hWnd} was not in front after either quiet raise or the checks that followed them; falling back to the synthetic Alt press.");
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyEventFKeyUp, UIntPtr.Zero);
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        var accepted = SetForegroundWindow(hWnd);
        var inFront = accepted && GetForegroundWindow() == hWnd;
        if (inFront) log.Info($"Window {hWnd} came to the front after the synthetic Alt press.");
        else log.Warn($"SetForegroundWindow was refused for window {hWnd} by all three attempts (last accepted={accepted}).");
        return inFront;
    }

    /// <summary>Ask, and believe the answer only if the window really is in front.</summary>
    private static bool TrySetForeground(IntPtr hWnd) => SetForegroundWindow(hWnd) && GetForegroundWindow() == hWnd;

    /// <summary>
    /// Is the window in front, now or within the next <see cref="SettleChecks"/> ×
    /// <see cref="SettleDelayMs"/> ms? Asked between the rungs, so that neither a window
    /// that was already there nor an activation still in flight is mistaken for a refusal
    /// and answered with a keystroke.
    /// </summary>
    private static bool SettledInFront(IntPtr hWnd)
    {
        for (var i = 0; i < SettleChecks; i++)
        {
            if (GetForegroundWindow() == hWnd) return true;
            Thread.Sleep(SettleDelayMs);
        }
        return false;
    }

    /// <summary>
    /// Step 2: attach this thread's input to the thread that owns whatever is in front, so
    /// that for the length of the call this process counts as part of the foreground and
    /// SetForegroundWindow is allowed. The detach is in a finally — leaving two input
    /// queues joined would tie this console's keyboard state to another program's.
    /// </summary>
    private static bool TryAttachedSetForeground(IntPtr hWnd, PaladinLog log)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == hWnd) return false;

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ourThread) return false;

        var attached = AttachThreadInput(ourThread, foregroundThread, true);
        if (!attached) log.Debug($"AttachThreadInput to thread {foregroundThread} was refused; asking anyway.");
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
            return TrySetForeground(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// The first half of <see cref="KeepInFrontAsync"/>: wait up to
    /// <paramref name="appearTimeout"/> for the process's window and put it in front
    /// once. Returns the window (zero when none appeared) and whether it ended up in
    /// front.
    ///
    /// It is a member of its own because a dump has to WAIT for that first raise before
    /// it types, and must not wait for the guard loop that follows it: the guard runs for
    /// GameWindowGuardSeconds (30 s by default), which is a sixth of a dump's whole
    /// mission budget spent doing nothing.
    /// </summary>
    public static async Task<(IntPtr Window, bool Raised)> RaiseOnceAsync(
        int pid, TimeSpan appearTimeout, PaladinLog log, Action<string>? onRaised, CancellationToken ct)
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
            return (IntPtr.Zero, false);
        }

        if (!BringToFront(hWnd, log)) return (hWnd, false);
        log.Info($"Brought window {hWnd} of pid {pid} to the front.");
        onRaised?.Invoke("Brought Age of Empires IV to the front");
        return (hWnd, true);
    }

    /// <summary>
    /// The second half: for <paramref name="guard"/>, bring the process's window back
    /// whenever it is found minimised or swapped for another, at most
    /// <paramref name="maxRaises"/> times. Returns how many times it did.
    /// </summary>
    public static async Task<int> GuardAsync(
        int pid, IntPtr hWnd, TimeSpan guard, int maxRaises, PaladinLog log, Action<string>? onRaised, CancellationToken ct)
    {
        var raises = 0;
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

    /// <summary>
    /// Wait up to <paramref name="appearTimeout"/> for the process's window, bring it
    /// to the front, then for <paramref name="guard"/> bring it back whenever it is
    /// found minimised, at most <paramref name="maxRaises"/> times in all. Returns the
    /// number of times the window was put in front.
    /// </summary>
    public static async Task<int> KeepInFrontAsync(
        int pid, TimeSpan appearTimeout, TimeSpan guard, int maxRaises, PaladinLog log, Action<string>? onRaised, CancellationToken ct)
    {
        var (hWnd, raised) = await RaiseOnceAsync(pid, appearTimeout, log, onRaised, ct);
        if (hWnd == IntPtr.Zero) return 0;
        var first = raised ? 1 : 0;
        return first + await GuardAsync(pid, hWnd, guard, maxRaises - first, log, onRaised, ct);
    }
}
