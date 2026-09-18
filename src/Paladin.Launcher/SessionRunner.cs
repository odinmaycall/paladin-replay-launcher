using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Paladin.Core.Config;
using Paladin.Core.Dump;
using Paladin.Core.Logging;
using Paladin.Core.Model;
using Paladin.Core.Replay;
using Paladin.Core.Shield;
using Paladin.Core.Steam;
using Paladin.Launcher.Platform;

namespace Paladin.Launcher;

/// <summary>
/// The running game, handed to <see cref="SessionRunner.WhileRunning"/>: everything a
/// dump (or, later, a watch-along) needs to work with a game this runner started, and
/// nothing that would let it launch, quit or restore anything itself (§717 §3.2).
/// </summary>
/// <param name="Pid">The game process this session is watching.</param>
/// <param name="MainWindow">Its largest visible top-level window, or zero if none was found in time.</param>
/// <param name="ProcessSeenUtc">When the process first appeared: the zero of every offset in the record.</param>
/// <param name="ReplayName">The name the replay was placed under in playback\.</param>
public sealed record GameSessionContext(
    int Pid,
    IntPtr MainWindow,
    DateTime ProcessSeenUtc,
    string SessionId,
    string SessionDirectory,
    string Aoe4DocumentsPath,
    string ReplayName,
    int? ReplayBuild);

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

    /// <summary>
    /// Run something while the game is up, between "Age of Empires IV is running" and the
    /// wait for it to close, and let it say how the game should end (§717 §3.2). This is
    /// the one seam the dump needs: the launch, the Shield, the process monitor and the
    /// restore stay exactly as they are for a watch. Anything it throws is caught here,
    /// because an escape would skip the restore.
    /// </summary>
    public Func<GameSessionContext, CancellationToken, Task<ExitPolicy>>? WhileRunning { get; init; }

    /// <summary>
    /// Checked against the name the replay is about to be placed under; a non-null answer
    /// refuses the launch with that message. A dump uses it to prove the replay it
    /// downloaded is the game it was asked to dump (§717 §3.3 step 1).
    /// </summary>
    public Func<string, string?>? ReplayNameCheck { get; init; }

    /// <summary>A build mismatch is a warning for a watch and a hard stop for a dump (F9): the mission would never start.</summary>
    public bool RefuseOnBuildMismatch { get; init; }

    /// <summary>
    /// What to do with the game when the run is interrupted (§717 §2.5's Ctrl+C row:
    /// "stop typing, ... end the process after the grace, restore"). A watch leaves it
    /// alone — the user is watching their replay — but a dump's game was started only to
    /// be read, and it must be gone BEFORE the Shield restores: a -dev game still holding
    /// its console writes local.ini back over the restore when it is finally closed.
    /// </summary>
    public ExitPolicy CancelPolicy { get; init; } = ExitPolicy.WaitForUser;

    /// <summary>False when the caller has already printed its own banner (a dump's names the game, not the replay).</summary>
    public bool PrintHeader { get; init; } = true;

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
        if (PrintHeader) _ui.Header(DescribeRequest(request));

        // ---- 1. Environment ------------------------------------------------------
        var env = Aoe4Locator.Detect(_config, _log);
        if (!env.CanProtect)
        {
            foreach (var problem in env.Problems) _ui.Fail(problem);
            _ui.Fail("Refusing to launch: Paladin Shield cannot protect settings it cannot find.");
            return ExitCodes.EnvironmentNotFound;
        }
        // The console shows a path with no username and no personal folder names
        // (people screenshot and stream this window); the full path is in the log.
        _ui.Ok($"Age of Empires IV found ({PathDisplay.ForConsole(env.Aoe4DocumentsPath!)})");
        _log.Debug($"AoE4 Documents: {env.Aoe4DocumentsPath}");
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

        // Held outside the try so the cancellation handler below can end the game it
        // started before the Shield restores anything (CancelPolicy).
        GameProcessMonitor? monitor = null;
        GameProcessMonitor.GameProcessInfo? game = null;

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

            // Say up front whether this replay can play on the installed build. Left to
            // itself the mismatch surfaces as "Due to a recent update, the replay is no
            // longer available" inside AoE4, minutes later, with nothing to act on.
            var compatibility = BuildCompatibility.Compare(replay.GameBuild, env.Aoe4GameBuild);
            _log.Info($"Build check: replay {replay.GameBuild?.ToString() ?? "?"} vs game {env.Aoe4GameBuild?.ToString() ?? "?"} -> {compatibility.Verdict}");
            session.ReplayGameBuild = replay.GameBuild;
            session.InstalledGameBuild = env.Aoe4GameBuild;

            if (compatibility.ShouldWarn)
            {
                _ui.Warn(compatibility.Message);
                foreach (var line in BuildCompatibility.SuggestionsFor(compatibility)) _ui.Note(line);
                session.Notes.Add($"Build mismatch: replay {replay.GameBuild}, game {env.Aoe4GameBuild}.");

                // F9: a dump that cannot reach "Starting mission" is two wasted minutes and
                // a confusing failure; refuse before anything is placed or launched.
                if (RefuseOnBuildMismatch)
                {
                    _ui.Fail("A dump needs a replay this build can play; nothing was launched.");
                    session.Outcome = SessionOutcome.AbandonedByUser;
                    session.RestorePending = false;
                    _store.Save(session);
                    return ExitCodes.ReplayUnavailable;
                }
            }
            else if (compatibility.Verdict == BuildCompatibility.Verdict.Match)
            {
                _ui.Ok($"Replay matches your game build ({replay.GameBuild})");
            }

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

            if (ReplayNameCheck?.Invoke(replayName) is { } nameProblem)
            {
                // Nothing has been placed or launched yet; the snapshot above is simply
                // checked and dropped, the way a dry run's is.
                _ui.Fail(nameProblem);
                session.Notes.Add(nameProblem);
                await FinishAsync(session, env, snapshots, restorer, placedReplay: null, ct, gameRan: false);
                return ExitCodes.ReplayUnavailable;
            }

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

            monitor = new GameProcessMonitor(_config.GameProcessNames, env.Aoe4GameExePath, _log);
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
            game = await monitor.WaitForStartAsync(
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
                var processSeen = DateTime.UtcNow;
                if (_config.BringGameToFront)
                {
                    var appear = TimeSpan.FromSeconds(_config.GameStartTimeoutSeconds);

                    if (WhileRunning is null)
                    {
                        // A watch: off the main flow, because the exit wait below must start
                        // now and the window can take a while to exist. Failures only go to
                        // the log, and nothing here can stop the session.
                        var guard = TimeSpan.FromSeconds(Math.Max(0, _config.GameWindowGuardSeconds));
                        Detach(GameWindow.KeepInFrontAsync(game.Pid, appear, guard, 3, _log, msg => _ui.Ok(msg), ct));
                    }
                    else
                    {
                        // A dump types into that window, so it WAITS for the first raise —
                        // and takes only that one. The guard loop is not run beside it: it
                        // raises by pressing Alt, and an Alt from another thread in the
                        // middle of the dump's Ctrl+V is the half-typed line §3.6 exists to
                        // prevent. The dump has its own guard — every sequence checks the
                        // foreground and re-raises once (KeyboardInjector.Deliver).
                        //
                        // A raise that fails is a warning, never the end of the session:
                        // the focus check before each line is what actually decides.
                        try
                        {
                            await GameWindow.RaiseOnceAsync(game.Pid, appear, _log, msg => _ui.Ok(msg), ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _log.Warn($"Bringing the game to the front failed: {ex.Message}");
                        }
                    }
                }
                _ui.RunningBanner();

                var exit = ExitPolicy.WaitForUser;
                if (WhileRunning is not null)
                {
                    var context = new GameSessionContext(
                        game.Pid, GameWindow.FindMainWindow(game.Pid), processSeen, sessionId,
                        _store.SessionDirectory(sessionId), env.Aoe4DocumentsPath!, replayName, replay.GameBuild);
                    exit = await RunWhileRunningAsync(context, ct);
                }

                if (exit.Action == ExitAction.EndProcess)
                    await EndGameAsync(monitor, game, exit.Grace, ct);

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

            // §2.5's Ctrl+C row: the game this run started for a dump is ended — after
            // the grace, as a finished dump's is — BEFORE the settings are compared and
            // put back. A -dev game left running rewrites local.ini (consolehistory
            // included) when it is finally closed, which would undo the restore the
            // console is about to report. A watch's CancelPolicy leaves the game alone.
            if (CancelPolicy.Action == ExitAction.EndProcess && monitor is not null && game is not null)
            {
                session.Notes.Add("Interrupted while the dump was running; the game was ended before the restore.");
                await EndGameAsync(monitor, game, CancelPolicy.Grace, CancellationToken.None);
            }

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
    /// Let a background task run on without awaiting it, but never in silence: a failure
    /// goes to the log. A cancelled task is not a failure and says nothing.
    /// </summary>
    private void Detach(Task task) =>
        _ = task.ContinueWith(
            t => _log.Warn($"Bringing the game to the front failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>
    /// The hook, wrapped. Nothing it does may stop the restore: an exception here would
    /// land in RunAsync's unhandled-error catch, which reports and saves but runs no
    /// restore, so it is caught, reported and turned into "leave the game to the user".
    /// Ctrl+C is not caught: cancellation belongs to RunAsync's own handler.
    /// </summary>
    private async Task<ExitPolicy> RunWhileRunningAsync(GameSessionContext context, CancellationToken ct)
    {
        try
        {
            return await WhileRunning!(context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error("The while-running hook failed", ex);
            _ui.Fail($"Something went wrong while the game was running: {ex.Message}");
            _ui.Note("Your settings are still checked and restored below.");
            return ExitPolicy.WaitForUser;
        }
    }

    /// <summary>
    /// D1 amended: the launcher ends the game itself. It waits the grace first — after a
    /// fatal Scar error the game closes in seconds by itself — and only then kills the one
    /// process this session started, re-checking that it is still the game's own exe.
    /// A replay has nothing to save, and the Shield restores after any exit.
    /// </summary>
    private async Task EndGameAsync(GameProcessMonitor monitor, GameProcessMonitor.GameProcessInfo game, TimeSpan grace, CancellationToken ct)
    {
        _ui.Pending(Paladin.Core.Dump.DumpConsoleText.ClosingTheGame);

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline)
        {
            if (!monitor.IsRunning(game))
            {
                _log.Info($"The game closed by itself within the {grace.TotalSeconds:0} s grace.");
                return;
            }
            await Task.Delay(500, ct);
        }

        if (!monitor.IsRunning(game)) return;

        _ui.Warn(Paladin.Core.Dump.DumpConsoleText.EndedTheGame((int)grace.TotalSeconds));
        if (!monitor.TryEndProcess(game, out var detail))
            _ui.Warn($"The game could not be ended ({detail}); waiting for it to close.");
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
        // The console shows a path with no username and no personal folder names
        // (people screenshot and stream this window); the full path is in the log.
        _ui.Ok($"Age of Empires IV found ({PathDisplay.ForConsole(env.Aoe4DocumentsPath!)})");
        _log.Debug($"AoE4 Documents: {env.Aoe4DocumentsPath}");

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

    // 10-18 are the dump failures of §717 §2.5. The numbers live in Paladin.Core beside
    // the texts they belong to (DumpExitCodes); these names are how the rest of the
    // launcher and the README refer to them.
    public const int DumpReplayNeverStarted = DumpExitCodes.ReplayNeverStarted;   // F1
    public const int DumpConsoleNeverOpened = DumpExitCodes.ConsoleNeverOpened;   // F2
    public const int DumpNotSignedIn = DumpExitCodes.NotSignedIn;                 // F3
    public const int DumpDefinitionDropped = DumpExitCodes.DefinitionDropped;     // F4
    public const int DumpIncomplete = DumpExitCodes.Incomplete;                   // F5
    public const int DumpFatalScarError = DumpExitCodes.FatalScarError;           // F6
    public const int DumpFocusLost = DumpExitCodes.FocusLost;                     // F7
    public const int DumpUploadRejected = DumpExitCodes.UploadRejected;           // F10
    public const int DumpGameAlreadyRunning = DumpExitCodes.GameAlreadyRunning;   // F8

    /// <summary>The command is parsed and understood but this build cannot run it yet.</summary>
    public const int NotImplemented = 19;
}
