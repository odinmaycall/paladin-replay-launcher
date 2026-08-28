using System.Runtime.Versioning;
using Paladin.Core.Config;
using Paladin.Core.Logging;
using Paladin.Core.Model;
using Paladin.Core.Shield;

namespace Paladin.Launcher;

/// <summary>
/// Finishes sessions that never got to restore — AoE4 crashed, the launcher was killed,
/// Windows restarted, the power went out.
///
/// Recovery is deliberately more cautious than a normal restore:
///  - only this machine and this user's own sessions are ever considered;
///  - a file is only touched when it still differs from its pre-launch hash;
///  - a file modified well after the session was last alive is treated as a deliberate
///    later edit by the user and left alone unless they explicitly say otherwise;
///  - the user is shown the list and asked before anything is written.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RecoveryRunner
{
    /// <summary>See <see cref="RecoveryTriage.DefaultLaterEditGrace"/> for the reasoning.</summary>
    public static readonly TimeSpan LaterEditGrace = RecoveryTriage.DefaultLaterEditGrace;

    private readonly LauncherConfig _config;
    private readonly PaladinLog _log;
    private readonly IShieldUi _ui;
    private readonly SessionStore _store;

    public RecoveryRunner(LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store)
    {
        _config = config;
        _log = log;
        _ui = ui;
        _store = store;
    }

    /// <summary>Returns the number of sessions still unresolved after the attempt.</summary>
    public int RunPending(bool interactive)
    {
        var pending = _store.FindPendingRestores(SessionRunner.CurrentUserSid(), Environment.MachineName);
        if (pending.Count == 0)
        {
            _log.Debug("No sessions are pending restore.");
            return 0;
        }

        _ui.Warn($"{pending.Count} previous replay session(s) did not finish restoring your settings.");
        var unresolved = 0;

        foreach (var session in pending)
        {
            if (!RecoverOne(session, interactive)) unresolved++;
        }

        return unresolved;
    }

    private bool RecoverOne(SessionRecord session, bool interactive)
    {
        _ui.Note($"Session {session.SessionId} started {session.StartedUtc:yyyy-MM-dd HH:mm}Z, replay: {session.ReplaySource.Kind}:{session.ReplaySource.Value}");

        if (!Directory.Exists(session.BackupPath))
        {
            _ui.Fail($"Its backup folder is missing ({session.BackupPath}); nothing can be recovered. Marking it resolved.");
            session.RestorePending = false;
            session.Notes.Add("Recovery abandoned: backup folder missing.");
            session.Outcome = SessionOutcome.RestoreFailed;
            _store.Save(session);
            return false;
        }

        if (!Directory.Exists(session.Aoe4DocumentsPath))
        {
            _ui.Fail($"Its AoE4 Documents folder no longer exists ({session.Aoe4DocumentsPath}). Leaving the backup in place.");
            return false;
        }

        var policy = new ProtectionPolicy(
            session.ProtectedPatterns.Count > 0 ? session.ProtectedPatterns : _config.EffectiveProtectedPatterns(),
            _config.ExcludedPatterns);

        var snapshots = new SnapshotService(policy, _log, _config.MaxProtectedFileBytes, _config.VolatileKeys);
        var current = snapshots.Capture(session.Aoe4DocumentsPath);
        var changes = ChangeDetector.Compare(session.PreLaunch, current);

        var (actionable, deferred) = RecoveryTriage.Triage(changes, current, session.LastHeartbeatUtc, LaterEditGrace);

        foreach (var skipped in deferred)
            _ui.Note($"Left alone (changed long after the session): {skipped.RelativePath}");

        if (actionable.Count == 0)
        {
            _ui.Ok("Your settings already match the pre-replay state. Nothing to restore.");
            session.RestorePending = false;
            session.RestoreCompletedUtc = DateTime.UtcNow;
            session.Outcome = SessionOutcome.RecoveredAfterCrash;
            session.Notes.Add("Recovery found no differences.");
            _store.Save(session);
            CleanUpStrandedReplay(session);
            return true;
        }

        _ui.Warn($"{actionable.Count} protected file(s) still differ from before that replay:");
        foreach (var change in actionable.Take(12)) _ui.Note(change.ToString());
        if (actionable.Count > 12) _ui.Note($"... and {actionable.Count - 12} more");

        if (interactive && !_ui.Confirm("Restore these files from the pre-replay backup?", defaultAnswer: true))
        {
            _ui.Note("Left as they are. The backup stays at:");
            _ui.Note($"  {session.BackupPath}");
            session.Notes.Add($"Recovery declined by the user at {DateTime.UtcNow:O}.");
            _store.Save(session);
            return false;
        }

        var restorer = new RestoreService(policy, _log, _config.QuarantineCreatedFiles);
        session.Restored = restorer.Restore(
            session.Aoe4DocumentsPath, session.BackupPath, session.QuarantinePath, session.PreLaunch, actionable);

        var failures = session.Restored.Where(r => r.Action == RestoreAction.Failed).ToList();
        if (failures.Count == 0)
        {
            _ui.Ok($"Recovered {session.Restored.Count(r => r.Verified)} file(s) from session {session.SessionId}.");
            session.RestorePending = false;
            session.RestoreCompletedUtc = DateTime.UtcNow;
            session.Outcome = SessionOutcome.RecoveredAfterCrash;
            _store.Save(session);
            CleanUpStrandedReplay(session);
            return true;
        }

        _ui.Fail($"{failures.Count} file(s) could not be recovered. The backup is kept at:");
        _ui.Note($"  {session.BackupPath}");
        foreach (var failure in failures) _ui.Note(failure.ToString());
        session.Outcome = SessionOutcome.RestoreFailed;
        _store.Save(session);
        return false;
    }

    private void CleanUpStrandedReplay(SessionRecord session)
    {
        if (!_config.CleanUpPreparedReplay) return;
        if (!session.PreparedReplayOwnedByLauncher || session.PreparedReplayPath is null) return;

        try
        {
            if (File.Exists(session.PreparedReplayPath))
            {
                File.Delete(session.PreparedReplayPath);
                _log.Info($"Removed the replay left behind by session {session.SessionId}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not remove the stranded replay: {ex.Message}");
        }
    }
}
