using Paladin.Core.Model;

namespace Paladin.Core.Shield;

public static class ChangeDetector
{
    /// <summary>
    /// Diffs two snapshots of the same protected set. Pure function — no file access —
    /// so the whole change-detection rulebook is unit testable.
    /// </summary>
    public static List<FileChange> Compare(IEnumerable<FileSnapshot> before, IEnumerable<FileSnapshot> after)
    {
        var beforeMap = before.ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase);
        var afterMap = after.ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase);

        var changes = new List<FileChange>();

        foreach (var (path, b) in beforeMap)
        {
            if (afterMap.TryGetValue(path, out var a))
            {
                changes.Add(new FileChange
                {
                    RelativePath = path,
                    Kind = ClassifyModification(b, a),
                    BeforeSha256 = b.Sha256,
                    AfterSha256 = a.Sha256,
                    BeforeSizeBytes = b.SizeBytes,
                    AfterSizeBytes = a.SizeBytes,
                });
            }
            else
            {
                changes.Add(new FileChange
                {
                    RelativePath = path,
                    Kind = ChangeKind.Removed,
                    BeforeSha256 = b.Sha256,
                    BeforeSizeBytes = b.SizeBytes,
                });
            }
        }

        foreach (var (path, a) in afterMap)
        {
            if (beforeMap.ContainsKey(path)) continue;
            changes.Add(new FileChange
            {
                RelativePath = path,
                Kind = ChangeKind.Created,
                AfterSha256 = a.Sha256,
                AfterSizeBytes = a.SizeBytes,
            });
        }

        changes.Sort(static (x, y) => string.CompareOrdinal(x.RelativePath, y.RelativePath));
        return changes;
    }

    /// <summary>
    /// Decides whether a differing file represents a real settings change or only the
    /// bookkeeping Age of Empires IV rewrites on every launch.
    ///
    /// The fallback is deliberately cautious: without a semantic hash on BOTH sides
    /// (binary files, unreadable files) any difference counts as a real modification.
    /// </summary>
    private static ChangeKind ClassifyModification(FileSnapshot before, FileSnapshot after)
    {
        if (string.Equals(after.Sha256, before.Sha256, StringComparison.OrdinalIgnoreCase))
            return ChangeKind.Unchanged;

        if (before.SemanticSha256 is null || after.SemanticSha256 is null)
            return ChangeKind.Modified;

        return string.Equals(after.SemanticSha256, before.SemanticSha256, StringComparison.OrdinalIgnoreCase)
            ? ChangeKind.BookkeepingOnly
            : ChangeKind.Modified;
    }

    /// <summary>Anything at all differs, bookkeeping included.</summary>
    public static bool AnythingChanged(IEnumerable<FileChange> changes) =>
        changes.Any(c => c.Kind != ChangeKind.Unchanged);

    /// <summary>A real settings change happened — the signal worth acting on.</summary>
    public static bool AnythingMeaningfullyChanged(IEnumerable<FileChange> changes) =>
        changes.Any(IsMeaningful);

    /// <summary>Changes worth restoring. Bookkeeping is left exactly as the game wrote it.</summary>
    public static bool IsMeaningful(FileChange change) =>
        change.Kind is ChangeKind.Modified or ChangeKind.Created or ChangeKind.Removed;
}
