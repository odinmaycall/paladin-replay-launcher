using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Paladin.Core.Config;
using Paladin.Core.Logging;
using Paladin.Core.Model;
using Paladin.Core.Replay;
using Paladin.Core.Shield;
using Paladin.Core.Steam;
using Paladin.Launcher.Platform;

namespace Paladin.Launcher;

/// <summary>
/// One replay session, start to finish. The ordering here is the safety contract:
/// nothing is launched until a verified backup exists, and restore_pending is written
/// to disk before AoE4 is allowed to start.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionRunner
{
    private readonly LauncherConfig _config;
    private readonly PaladinLog _log;
    private readonly IShieldUi _ui;
    private readonly SessionStore _store;
    private readonly ReplayProviderRegistry _providers;

    public bool DryRun { get; init; }

    public SessionRunner(
        LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store, ReplayProviderRegistry providers)
    {
        _config = config;
        _log = log;
        _ui = ui;
        _store = store;
        _providers = providers;
    }

    public async Task<int> RunAsync(ReplayRequest request, CancellationToken ct)
    {
        _ui.Header(DescribeRequest(request));

        // ---- 1. Environment ------------------------------------------------------
        var env = Aoe4Locator.Detect(_config, _log);
        if (!env.CanProtect)
        {
            foreach (var problem in env.Problems) _ui.Fail(problem);
            _ui.Fail("Refusing to launch: Paladin Shield cannot protect settings it cannot find.");
            return ExitCodes.EnvironmentNotFound;
        }
        _ui.Ok($"Age of Empires IV found ({env.Aoe4DocumentsPath})");
        foreach (var problem in env.Problems) _ui.Warn(problem);

        if (!env.CanLaunch && !DryRun)
        {
            _ui.Fail("Steam could not be found, so the replay cannot be launched.");
            return ExitCodes.EnvironmentNotFound;
        }

        // ---- 2. Session scaffolding ----------------------------------------------
        var sessionId = SessionStore.NewSessionId(DateTime.UtcNow);
        var session = _store.Create(sessionId, DateTime.UtcNow, CurrentUserSid(), Environment.MachineName);
        session.Aoe4DocumentsPath = env.Aoe4DocumentsPath!;
        session.ReplaySource = new ReplaySourceRecord { Kind = request.Kind, Value = request.Value };
        _log.AddFile(_store.SessionLogFile(sessionId));
        _log.Info($"Session {sessionId} started.");

        var policy = new ProtectionPolicy(_config.EffectiveProtectedPatterns(), _config.ExcludedPatterns);
        session.ProtectedPatterns = policy.IncludePatterns.ToList();

        var snapshots = new SnapshotService(policy, _log, _config.MaxProtectedFileBytes, _config.VolatileKeys);
        var restorer = new RestoreService(policy, _log, _config.QuarantineCreatedFiles);

        string? placedReplay = null;

        try
        {
            // ---- 3. Acquire the replay -------------------------------------------
            _ui.Pending("Preparing replay ...");
            var workDir = Path.Combine(_store.SessionDirectory(sessionId), "replay");
            AcquiredReplay replay;
            try
            {
                replay = await _providers.AcquireAsync(request, workDir, ct);
            }
            catch (ReplayAcquisitionException ex)
            {
                _ui.Fail($"Replay could not be prepared: {ex.Message}");
                _log.Error("Replay acquisition failed", ex);
                session.Outcome = SessionOutcome.AbandonedByUser;
                session.Notes.Add($"Replay acquisition failed: {ex.Message}");
                _store.Save(session);
                return ExitCodes.ReplayUnavailable;
            }
            _ui.Ok($"Replay prepared ({replay.SizeBytes:N0} bytes)");

            // ---- 4. Snapshot BEFORE anything is placed or launched ----------------
            _ui.Pending("Creating settings snapshot ...");
            session.PreLaunch = snapshots.Capture(env.Aoe4DocumentsPath!);
            if (session.PreLaunch.Count == 0)
            {
                _ui.Warn("No protected settings files matched the current patterns. Check config.json.");
                session.Notes.Add("Snapshot matched zero files.");
            }

            var backupOk = snapshots.WriteBackup(env.Aoe4DocumentsPath!, session.BackupPath, session.PreLaunch);
            _store.Save(session);

            if (!backupOk)
            {
                _ui.Fail("The settings backup could not be fully verified, so the replay will not be launched.");
                _ui.Note($"Partial backup kept at {session.BackupPath}");
                session.Outcome = SessionOutcome.RestoreFailed;
                session.Notes.Add("Aborted before launch: backup verification failed.");
                _store.Save(session);
                return ExitCodes.BackupFailed;
            }
            _ui.Ok($"Settings snapshot created ({session.PreLaunch.Count} files protected)");

            // ---- 5. Place the replay where AoE4 looks ----------------------------
            var replayName = LaunchCommandBuilder.ReplayFileNameFor(replay.SuggestedFileName, _config.StripReplayExtension);
            Directory.CreateDirectory(env.PlaybackPath!);
            placedReplay = Path.Combine(env.PlaybackPath!, replayName);

            var overwritingExisting = File.Exists(placedReplay);
            if (overwritingExisting)
            {
                // Never clobber one of the user's own replays.
                replayName += "_paladin";
                placedReplay = Path.Combine(env.PlaybackPath!, replayName);
                _log.Warn($"A file of that name already existed in playback/; using '{replayName}' instead.");
            }

            File.Copy(replay.LocalPath, placedReplay, overwrite: false);
            session.PreparedReplayPath = placedReplay;
            session.PreparedReplayOwnedByLauncher = true;
            _ui.Ok($"Replay placed in the AoE4 playback folder as '{replayName}'");
            _log.Info($"Placed replay at {placedReplay}");

            // ---- 6. Arm crash recovery, then launch -------------------------------
            var arguments = LaunchCommandBuilder.BuildSteamArguments(_config, replayName);
            session.LaunchExecutable = env.SteamExePath;
            session.LaunchArguments = arguments;
            session.RestorePending = true;          // written to disk BEFORE the game starts
            _store.Save(session);
            _log.Info($"restore_pending = true (session {sessionId})");
            _log.Info($"Launch command: \"{env.SteamExePath}\" {arguments}");

            if (DryRun)
            {
                _ui.Warn("Dry run: Age of Empires IV was NOT launched.");
                _ui.Note($"Would have run: \"{env.SteamExePath}\" {arguments}");
                await FinishAsync(session, env, snapshots, restorer, placedReplay, ct, gameRan: false);
                return ExitCodes.Ok;
            }

            var monitor = new GameProcessMonitor(_config.GameProcessNames, env.Aoe4GameExePath, _log);
            var preexisting = monitor.Snapshot().Select(p => p.Pid).ToHashSet();
            if (preexisting.Count > 0)
                _ui.Warn($"Age of Empires IV already appears to be running ({preexisting.Count} process(es)). Close it first for a clean session.");

            _ui.Pending("Launching Age of Empires IV ...");
            if (!StartSteam(env.SteamExePath!, arguments))
            {
                _ui.Fail("Steam refused to start. Restoring settings and stopping.");
                await FinishAsync(session, env, snapshots, restorer, placedReplay, ct, gameRan: false);
                return ExitCodes.LaunchFailed;
            }

            // ---- 7. Monitor -------------------------------------------------------
            var game = await monitor.WaitForStartAsync(
                preexisting, TimeSpan.FromSeconds(_config.GameStartTimeoutSeconds), ct);

            if (game is null)
            {
                _ui.Warn($"Age of Empires IV did not start within {_config.GameStartTimeoutSeconds}s.");
                _ui.Note("Running the settings check anyway — nothing will be restored if nothing changed.");
                session.Notes.Add("Game process never appeared.");
            }
            else
            {
                _ui.Ok("Age of Empires IV is running");
                _ui.RunningBanner();
                await monitor.WaitForExitAsync(game, ct, onHeartbeat: () => _store.Heartbeat(session));
                _ui.Ok("Replay finished");
            }

            // ---- 8. Compare and restore -------------------------------------------
            await FinishAsync(session, env, snapshots, restorer, placedReplay, ct, gameRan: game is not null);
            return session.Outcome == SessionOutcome.RestoreFailed ? ExitCodes.RestoreFailed : ExitCodes.Ok;
        }
        catch (OperationCanceledException)
        {
            _ui.Warn("Interrupted. Running the settings check before exiting.");
            _log.Warn("Session cancelled by the user; attempting restore.");
            await FinishAsync(session, env, snapshots, restorer, placedReplay, CancellationToken.None, gameRan: true);
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            _log.Error("Unhandled session error", ex);
            _ui.Fail($"Something went wrong: {ex.Message}");
            _ui.Note($"Your backup is safe at {session.BackupPath}");
            session.Notes.Add($"Unhandled error: {ex}");
            session.Outcome = SessionOutcome.RestoreFailed;
            _store.Save(session);
            return ExitCodes.UnexpectedError;
        }
    }

    /// <summary>
    /// Observation mode: snapshot, wait for AoE4 to be launched and closed by the user
    /// however they like, then report what changed — without launching anything itself.
    ///
    /// This is the control experiment. A replay session tells you which files changed
    /// during a replay; it cannot tell you whether those same files change on an ordinary
    /// launch. Run this against a normal game start and the difference between the two
    /// lists is the actual effect of replay/-dev launching.
    /// </summary>
    public async Task<int> RunObserveAsync(CancellationToken ct)
    {
        _ui.Header("observation mode — Paladin will not launch anything");

        var env = Aoe4Locator.Detect(_config, _log);
        if (!env.CanProtect)
        {
            foreach (var problem in env.Problems) _ui.Fail(problem);
            return ExitCodes.EnvironmentNotFound;
        }
        _ui.Ok($"Age of Empires IV found ({env.Aoe4DocumentsPath})");

        var sessionId = SessionStore.NewSessionId(DateTime.UtcNow);
        var session = _store.Create(sessionId, DateTime.UtcNow, CurrentUserSid(), Environment.MachineName);
        session.Aoe4DocumentsPath = env.Aoe4DocumentsPath!;
        session.ReplaySource = new ReplaySourceRecord { Kind = "observe", Value = "(no replay — control run)" };
        _log.AddFile(_store.SessionLogFile(sessionId));

        var policy = new ProtectionPolicy(_config.EffectiveProtectedPatterns(), _config.ExcludedPatterns);
        session.ProtectedPatterns = policy.IncludePatterns.ToList();
        var snapshots = new SnapshotService(policy, _log, _config.MaxProtectedFileBytes, _config.VolatileKeys);
        var restorer = new RestoreService(policy, _log, _config.QuarantineCreatedFiles);

        _ui.Pending("Creating settings snapshot ...");
        session.PreLaunch = snapshots.Capture(env.Aoe4DocumentsPath!);
        if (!snapshots.WriteBackup(env.Aoe4DocumentsPath!, session.BackupPath, session.PreLaunch))
        {
            _ui.Fail("The settings backup could not be fully verified. Stopping.");
            session.Outcome = SessionOutcome.RestoreFailed;
            _store.Save(session);
            return ExitCodes.BackupFailed;
        }
        _ui.Ok($"Settings snapshot created ({session.PreLaunch.Count} files protected)");

        session.RestorePending = true;
        _store.Save(session);

        var monitor = new GameProcessMonitor(_config.GameProcessNames, env.Aoe4GameExePath, _log);
        var preexisting = monitor.Snapshot().Select(p => p.Pid).ToHashSet();
        if (preexisting.Count > 0)
            _ui.Warn("Age of Empires IV is already running. Close it first, or the control run will not measure a clean start.");

        Console.WriteLine();
        _ui.Note("Now start Age of Empires IV yourself, the way you normally do.");
        _ui.Note("Play or sit in the menu briefly, then quit the game.");
        _ui.Note("Paladin will report exactly what changed, and ask before putting anything back.");
        Console.WriteLine();

        var game = await monitor.WaitForStartAsync(
            preexisting, TimeSpan.FromSeconds(_config.ObserveStartTimeoutSeconds), ct,
            onStillWaiting: remaining => _ui.Pending(
                $"Still waiting for Age of Empires IV — {(int)remaining.TotalMinutes} min left. Press Ctrl+C to give up."));

        if (game is null)
        {
            _ui.Warn($"Age of Empires IV did not start within {_config.ObserveStartTimeoutSeconds / 60} minutes, so there is nothing to compare.");
            _ui.Note("Run --observe again and start the game while it waits.");
            session.Notes.Add("Observation: game never appeared.");
        }
        else
        {
            _ui.Ok("Age of Empires IV is running — waiting for it to close.");
            await monitor.WaitForExitAsync(game, ct, onHeartbeat: () => _store.Heartbeat(session));
        }

        await FinishAsync(session, env, snapshots, restorer, placedReplay: null, ct,
            gameRan: game is not null, promptBeforeRestore: true);

        return ExitCodes.Ok;
    }

    /// <summary>
    /// The tail of every path through a session: re-snapshot, diff, restore, verify,
    /// clear the crash flag, clean up. Written so it is safe to call more than once.
    /// </summary>
    private Task FinishAsync(
        SessionRecord session,
        Aoe4Environment env,
        SnapshotService snapshots,
        RestoreService restorer,
        string? placedReplay,
        CancellationToken ct,
        bool gameRan,
        bool promptBeforeRestore = false)
    {
        if (gameRan && _config.PostExitSettleSeconds > 0)
        {
            _log.Debug($"Settling {_config.PostExitSettleSeconds}s so AoE4 can finish flushing its config files.");
            try { Task.Delay(TimeSpan.FromSeconds(_config.PostExitSettleSeconds), ct).Wait(ct); }
            catch (OperationCanceledException) { }
        }

        _ui.Pending("Checking protected settings ...");
        var after = snapshots.Capture(session.Aoe4DocumentsPath);
        session.Changes = ChangeDetector.Compare(session.PreLaunch, after);

        foreach (var change in session.Changes.Where(c => c.Kind != ChangeKind.Unchanged))
            _log.Info($"CHANGED  {change}");

        // Age of Empires IV rewrites timestamps and run counters on every launch, replay
        // or not. Those are reported but never acted on; only a real settings difference
        // counts as something to restore.
        var bookkeeping = session.Changes.Where(c => c.Kind == ChangeKind.BookkeepingOnly).ToList();
        var changed = session.Changes.Where(ChangeDetector.IsMeaningful).ToList();

        if (changed.Count == 0)
        {
            _ui.Ok("Settings checked — no settings were changed");
            if (bookkeeping.Count > 0)
                _ui.Note($"({bookkeeping.Count} file(s) had only timestamps or run counters updated — normal on any launch, left as the game wrote them.)");
            session.Restored.Clear();
            session.Outcome = SessionOutcome.RestoredClean;
        }
        else
        {
            _ui.Warn($"{changed.Count} protected file(s) were genuinely changed while AoE4 was open:");
            foreach (var change in changed.Take(12)) _ui.Note(change.ToString());
            if (changed.Count > 12) _ui.Note($"... and {changed.Count - 12} more (see the log)");
            if (bookkeeping.Count > 0)
                _ui.Note($"(plus {bookkeeping.Count} bookkeeping-only change(s), which are left alone.)");

            // Capture what the game left behind BEFORE overwriting it. Restoring destroys
            // the only copy of the modified file, and that copy is the evidence for
            // whether this was damage or routine bookkeeping.
            if (_config.SaveChangedCopies)
            {
                var saved = SaveChangedCopies(session, changed);
                if (saved > 0)
                    _ui.Note($"Kept {saved} changed file(s) for inspection: {session.ChangedCopiesPath}");
            }

            if (promptBeforeRestore &&
                !_ui.Confirm("Restore these files to their pre-launch state?", defaultAnswer: true))
            {
                _ui.Warn("Left as they are, at your request. Backup kept at:");
                _ui.Note($"  {session.BackupPath}");
                session.Notes.Add("Restore declined by the user.");
                session.Outcome = SessionOutcome.AbandonedByUser;
                session.RestorePending = false;
                session.EndedUtc = DateTime.UtcNow;
                _store.Save(session);
                _ui.Done("Finished without restoring.");
                return Task.CompletedTask;
            }

            session.Restored = restorer.Restore(
                session.Aoe4DocumentsPath, session.BackupPath, session.QuarantinePath, session.PreLaunch, session.Changes);

            var failures = session.Restored.Where(r => r.Action == RestoreAction.Failed).ToList();
            if (failures.Count == 0)
            {
                _ui.Ok($"Settings restored ({session.Restored.Count(r => r.Verified)} file(s) put back)");
                session.Outcome = SessionOutcome.RestoredChanges;
            }
            else
            {
                _ui.Fail($"{failures.Count} file(s) could not be restored.");
                foreach (var failure in failures) _ui.Note(failure.ToString());
                _ui.Fail($"Your pre-launch backup has been KEPT at:\n           {session.BackupPath}");
                _ui.Note("Copy those files back over your AoE4 Documents folder to recover manually.");
                session.Outcome = SessionOutcome.RestoreFailed;
            }
        }

        var restoreClean = session.Outcome != SessionOutcome.RestoreFailed;
        session.RestorePending = !restoreClean;
        session.RestoreCompletedUtc = restoreClean ? DateTime.UtcNow : null;
        session.EndedUtc = DateTime.UtcNow;
        _store.Save(session);
        _log.Info($"restore_pending = {session.RestorePending.ToString().ToLowerInvariant()} (session {session.SessionId})");

        CleanUp(session, placedReplay, restoreClean);
        _ui.Done(restoreClean ? "Done. Paladin Shield finished cleanly." : "Finished WITH ERRORS — backup retained, see above.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Copies each changed or created file, as the game left it, into &lt;session&gt;/changed/.
    /// Read-only with respect to the live folder; failures are logged and never block a restore.
    /// </summary>
    private int SaveChangedCopies(SessionRecord session, IReadOnlyList<FileChange> changed)
    {
        var saved = 0;
        foreach (var change in changed)
        {
            if (change.Kind == ChangeKind.Removed) continue;   // nothing left to copy

            var live = Path.Combine(session.Aoe4DocumentsPath, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(session.ChangedCopiesPath, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(live)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(live, target, overwrite: true);
                saved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not keep a copy of the changed {change.RelativePath}: {ex.Message}");
            }
        }
        if (saved > 0) _log.Info($"Kept {saved} changed file(s) in {session.ChangedCopiesPath} for comparison against the backup.");
        return saved;
    }

    private void CleanUp(SessionRecord session, string? placedReplay, bool restoreClean)
    {
        if (_config.CleanUpPreparedReplay && placedReplay is not null && session.PreparedReplayOwnedByLauncher)
        {
            try
            {
                if (File.Exists(placedReplay))
                {
                    File.Delete(placedReplay);
                    _log.Info($"Cleaned up the temporary replay at {placedReplay}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not remove the temporary replay: {ex.Message}");
            }
        }

        // A failed restore keeps everything. This is the "do not silently discard an
        // unresolved backup" rule and it overrides every cleanup setting.
        if (!restoreClean)
        {
            _log.Warn("Restore did not complete cleanly; the session folder and backup are retained.");
            return;
        }

        if (_config.CleanUpSessionOnSuccess)
        {
            try
            {
                var replayWork = Path.Combine(_store.SessionDirectory(session.SessionId), "replay");
                if (Directory.Exists(replayWork)) Directory.Delete(replayWork, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not clean the session working directory: {ex.Message}");
            }
            _store.Prune(_config.KeepRecentSessions);
        }
    }

    private bool StartSteam(string steamExe, string arguments)
    {
        try
        {
            var info = new ProcessStartInfo(steamExe, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(steamExe) ?? Environment.CurrentDirectory,
            };
            using var process = Process.Start(info);
            _log.Info($"Steam launch requested (pid {process?.Id.ToString() ?? "n/a"}).");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Error("Could not start Steam", ex);
            return false;
        }
    }

    private static string DescribeRequest(ReplayRequest request) => request.Kind switch
    {
        "local" => Path.GetFileName(request.Value),
        "url" => request.Value,
        _ => $"{request.Value} (via {request.Kind})",
    };

    public static string CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return Environment.UserName;
        }
    }
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int EnvironmentNotFound = 2;
    public const int ReplayUnavailable = 3;
    public const int BackupFailed = 4;
    public const int LaunchFailed = 5;
    public const int RestoreFailed = 6;
    public const int Cancelled = 7;
    public const int BadArguments = 8;
    public const int UnexpectedError = 9;
}
