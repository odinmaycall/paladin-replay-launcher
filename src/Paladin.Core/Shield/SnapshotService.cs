using Paladin.Core.Logging;
using Paladin.Core.Model;

namespace Paladin.Core.Shield;

public sealed class SnapshotService
{
    private readonly ProtectionPolicy _policy;
    private readonly PaladinLog _log;
    private readonly long _maxFileBytes;
    private readonly IReadOnlyList<string> _volatileKeys;

    public SnapshotService(ProtectionPolicy policy, PaladinLog log, long maxFileBytes, IEnumerable<string>? volatileKeys = null)
    {
        _policy = policy;
        _log = log;
        _maxFileBytes = maxFileBytes;
        _volatileKeys = (volatileKeys ?? VolatileContent.DefaultVolatileKeys).ToList();
    }

    /// <summary>Enumerate protected files under <paramref name="root"/>, hashing each. Read only.</summary>
    public List<FileSnapshot> Capture(string root)
    {
        var results = new List<FileSnapshot>();
        if (!Directory.Exists(root))
        {
            _log.Warn($"Snapshot root does not exist: {root}");
            return results;
        }

        foreach (var relative in EnumerateProtected(root))
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                var info = new FileInfo(full);
                if (info.Length > _maxFileBytes)
                {
                    _log.Warn($"Skipping protected file over size limit ({info.Length} bytes): {relative}");
                    continue;
                }
                results.Add(new FileSnapshot
                {
                    RelativePath = relative,
                    SizeBytes = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    Sha256 = FileHasher.HashFile(full),
                    SemanticSha256 = VolatileContent.TryComputeSemanticHash(full, relative, _volatileKeys, _maxFileBytes),
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not read protected file {relative}: {ex.Message}");
            }
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    /// <summary>
    /// Copies every snapshot entry into <paramref name="backupRoot"/>, preserving the relative
    /// structure, then re-hashes the copy. Returns false unless every file verified — the caller
    /// must not launch the game on a partial backup.
    /// </summary>
    public bool WriteBackup(string root, string backupRoot, List<FileSnapshot> snapshot)
    {
        Directory.CreateDirectory(backupRoot);
        var allVerified = true;

        foreach (var entry in snapshot)
        {
            var source = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            var target = Path.Combine(backupRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);

                var copyHash = FileHasher.HashFile(target);
                entry.BackupVerified = string.Equals(copyHash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!entry.BackupVerified)
                {
                    allVerified = false;
                    _log.Error($"Backup hash mismatch for {entry.RelativePath} (source {entry.Sha256}, copy {copyHash})");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allVerified = false;
                entry.BackupVerified = false;
                _log.Error($"Backup copy failed for {entry.RelativePath}", ex);
            }
        }

        _log.Info($"Backup: {snapshot.Count(s => s.BackupVerified)}/{snapshot.Count} files copied and verified into {backupRoot}");
        return allVerified;
    }

    private IEnumerable<string> EnumerateProtected(string root)
    {
        var pending = new Stack<string>();
        pending.Push("");

        while (pending.Count > 0)
        {
            var relDir = pending.Pop();
            var absDir = relDir.Length == 0 ? root : Path.Combine(root, relDir.Replace('/', Path.DirectorySeparatorChar));

            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(absDir);
                dirs = Directory.GetDirectories(absDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not enumerate {absDir}: {ex.Message}");
                continue;
            }

            foreach (var f in files)
            {
                var rel = Join(relDir, Path.GetFileName(f));
                if (_policy.IsProtected(rel)) yield return rel;
            }

            foreach (var d in dirs)
            {
                var rel = Join(relDir, Path.GetFileName(d));
                if (_policy.CouldContainProtected(rel)) pending.Push(rel);
            }
        }
    }

    private static string Join(string relDir, string name) => relDir.Length == 0 ? name : relDir + "/" + name;
}
