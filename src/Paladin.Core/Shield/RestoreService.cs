using Paladin.Core.Logging;
using Paladin.Core.Model;

namespace Paladin.Core.Shield;

public sealed class RestoreService
{
    private readonly ProtectionPolicy _policy;
    private readonly PaladinLog _log;
    private readonly bool _quarantineCreated;

    public RestoreService(ProtectionPolicy policy, PaladinLog log, bool quarantineCreatedFiles)
    {
        _policy = policy;
        _log = log;
        _quarantineCreated = quarantineCreatedFiles;
    }

    /// <summary>
    /// Puts the protected set back to exactly its pre-launch state.
    ///
    /// Safety rules enforced here, not by the caller:
    ///  - nothing outside the protection policy is ever written or moved;
    ///  - a file is only overwritten from a backup copy that was verified at snapshot time;
    ///  - every write is re-hashed against the recorded pre-launch hash;
    ///  - files created during the session are moved to quarantine, never hard-deleted.
    /// </summary>
    public List<RestoreResult> Restore(
        string root,
        string backupRoot,
        string quarantineRoot,
        IReadOnlyList<FileSnapshot> preLaunch,
        IReadOnlyList<FileChange> changes)
    {
        var results = new List<RestoreResult>();
        var preMap = preLaunch.ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            if (change.Kind == ChangeKind.Unchanged) continue;

            // Age of Empires IV rewrites timestamps and run counters on every launch.
            // Reverting those achieves nothing and would fight the game for no benefit,
            // so bookkeeping-only differences are logged and left exactly as written.
            if (change.Kind == ChangeKind.BookkeepingOnly)
            {
                _log.Debug($"Leaving bookkeeping-only change alone: {change.RelativePath}");
                continue;
            }

            if (!_policy.IsProtected(change.RelativePath))
            {
                // Should be unreachable: both snapshots came from the same policy.
                _log.Warn($"Refusing to act on unprotected path: {change.RelativePath}");
                results.Add(Fail(change.RelativePath, "path is not in the protected set"));
                continue;
            }

            switch (change.Kind)
            {
                case ChangeKind.Modified:
                case ChangeKind.Removed:
                    results.Add(RestoreFromBackup(root, backupRoot, change, preMap));
                    break;

                case ChangeKind.Created:
                    results.Add(_quarantineCreated
                        ? QuarantineFile(root, quarantineRoot, change.RelativePath)
                        : Skip(change.RelativePath, "created file left in place (quarantine disabled)"));
                    break;
            }
        }

        return results;
    }

    private RestoreResult RestoreFromBackup(
        string root, string backupRoot, FileChange change, Dictionary<string, FileSnapshot> preMap)
    {
        var rel = change.RelativePath;
        if (!preMap.TryGetValue(rel, out var expected))
            return Fail(rel, "no pre-launch snapshot entry");

        if (!expected.BackupVerified)
            return Fail(rel, "backup copy was never verified; refusing to restore from it");

        var backupFile = Path.Combine(backupRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        var liveFile = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(backupFile))
            return Fail(rel, $"backup file missing at {backupFile}");

        // Re-verify the backup right before using it. A backup that rotted on disk must
        // never be written over live settings.
        string backupHash;
        try { backupHash = FileHasher.HashFile(backupFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Fail(rel, $"backup unreadable: {ex.Message}"); }

        if (!string.Equals(backupHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            return Fail(rel, "backup no longer matches its recorded hash");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(liveFile)!);

            // Write beside the target then swap, so a crash mid-copy cannot leave a
            // half-written settings file where AoE4 expects a whole one.
            var staging = liveFile + ".paladin-tmp";
            File.Copy(backupFile, staging, overwrite: true);

            if (File.Exists(liveFile))
                File.Replace(staging, liveFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(staging, liveFile);

            File.SetLastWriteTimeUtc(liveFile, expected.ModifiedUtc);

            var verified = string.Equals(FileHasher.HashFile(liveFile), expected.Sha256, StringComparison.OrdinalIgnoreCase);
            var action = change.Kind == ChangeKind.Removed ? RestoreAction.Recreated : RestoreAction.Overwritten;

            if (!verified) return Fail(rel, "restored file did not match the expected hash");

            _log.Info($"Restored {rel} ({action})");
            return new RestoreResult { RelativePath = rel, Action = action, Verified = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(rel, ex.Message);
        }
    }

    private RestoreResult QuarantineFile(string root, string quarantineRoot, string rel)
    {
        var liveFile = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine(quarantineRoot, rel.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(liveFile))
            return Skip(rel, "created file already gone");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(liveFile, target, overwrite: true);
            _log.Info($"Quarantined newly created protected file {rel} -> {target}");
            return new RestoreResult
            {
                RelativePath = rel,
                Action = RestoreAction.Quarantined,
                Verified = File.Exists(target) && !File.Exists(liveFile),
                Detail = target,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(rel, ex.Message);
        }
    }

    private RestoreResult Fail(string rel, string detail)
    {
        _log.Error($"Restore failed for {rel}: {detail}");
        return new RestoreResult { RelativePath = rel, Action = RestoreAction.Failed, Verified = false, Detail = detail };
    }

    private static RestoreResult Skip(string rel, string detail) =>
        new() { RelativePath = rel, Action = RestoreAction.SkippedUnchanged, Verified = true, Detail = detail };
}
