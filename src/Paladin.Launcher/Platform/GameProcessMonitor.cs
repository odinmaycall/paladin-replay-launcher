using System.Diagnostics;
using System.Runtime.Versioning;
using Paladin.Core.Logging;

namespace Paladin.Launcher.Platform;

/// <summary>
/// Watches for the real Age of Empires IV process.
///
/// This deliberately does NOT watch steam.exe. Asking Steam to launch a game hands the
/// request to an already-running Steam client and the process we started exits within
/// seconds, long before the game has even shown a window. The thing worth waiting on is
/// the game binary itself (RelicCardinal.exe on a live install).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameProcessMonitor
{
    private readonly IReadOnlyList<string> _processNames;
    private readonly string? _expectedExePath;
    private readonly PaladinLog _log;

    public GameProcessMonitor(IReadOnlyList<string> processNames, string? expectedExePath, PaladinLog log)
    {
        _processNames = processNames;
        _expectedExePath = expectedExePath;
        _log = log;
    }

    public sealed record GameProcessInfo(int Pid, string ProcessName, DateTime StartTimeUtc);

    /// <summary>PIDs of matching processes running right now.</summary>
    public List<GameProcessInfo> Snapshot()
    {
        var found = new List<GameProcessInfo>();
        foreach (var name in _processNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (InvalidOperationException) { continue; }

            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (!MatchesExpectedPath(process)) continue;
                        found.Add(new GameProcessInfo(process.Id, process.ProcessName, process.StartTime.ToUniversalTime()));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Process exited between enumeration and inspection, or is not readable.
                    }
                }
            }
        }
        return found;
    }

    /// <summary>
    /// Waits for a matching process that was not in <paramref name="ignorePids"/> to appear.
    /// Returns null on timeout. Polling beats an event subscription here because WMI process
    /// events need elevation on some machines and we must never require admin.
    /// </summary>
    public async Task<GameProcessInfo?> WaitForStartAsync(
        IReadOnlySet<int> ignorePids, TimeSpan timeout, CancellationToken ct, Action<TimeSpan>? onStillWaiting = null)
    {
        var started = DateTime.UtcNow;
        var deadline = started + timeout;
        var announced = false;
        var nextNudge = started + TimeSpan.FromSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var candidate = Snapshot().FirstOrDefault(p => !ignorePids.Contains(p.Pid));
            if (candidate is not null)
            {
                _log.Info($"Age of Empires IV detected: {candidate.ProcessName} (pid {candidate.Pid}).");
                return candidate;
            }

            if (!announced)
            {
                _log.Info($"Waiting for Age of Empires IV to start (looking for {string.Join(", ", _processNames)}) ...");
                announced = true;
            }

            // A console that prints nothing for half an hour looks hung. Say something
            // periodically so it is obvious the wait is deliberate.
            if (DateTime.UtcNow >= nextNudge)
            {
                onStillWaiting?.Invoke(deadline - DateTime.UtcNow);
                nextNudge = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            }

            await Task.Delay(1000, ct);
        }
        return null;
    }

    /// <summary>
    /// Waits until the given process — and any other matching game process — has exited.
    /// Covers the case where the game relaunches itself, which the -dev flag is capable of.
    /// </summary>
    public async Task WaitForExitAsync(GameProcessInfo game, CancellationToken ct, Action? onHeartbeat = null)
    {
        try
        {
            using var process = Process.GetProcessById(game.Pid);
            _log.Info($"Monitoring pid {game.Pid}; Paladin Shield stays resident until Age of Empires IV closes.");

            while (!process.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                onHeartbeat?.Invoke();
                if (process.WaitForExit(5000)) break;
            }
        }
        catch (ArgumentException)
        {
            _log.Warn($"Process {game.Pid} was already gone when monitoring started.");
        }

        // Any sibling game process still up means the game is not really finished.
        while (Snapshot().Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            _log.Debug("Another Age of Empires IV process is still running; continuing to wait.");
            onHeartbeat?.Invoke();
            await Task.Delay(2000, ct);
        }

        _log.Info("Age of Empires IV has exited.");
    }

    private bool MatchesExpectedPath(Process process)
    {
        if (_expectedExePath is null) return true;
        try
        {
            var path = process.MainModule?.FileName;
            if (path is null) return true; // unreadable (elevated/32-bit); fall back to the name match
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(_expectedExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return true;
        }
    }
}
