namespace Paladin.Core.Model;

/// <summary>One protected file as it existed at snapshot time.</summary>
public sealed class FileSnapshot
{
    /// <summary>Path relative to the AoE4 Documents root, always '/'-separated.</summary>
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>
    /// Hash of the file with per-launch bookkeeping lines removed. Null for binary files
    /// or anything unreadable, in which case only <see cref="Sha256"/> can be trusted.
    /// </summary>
    public string? SemanticSha256 { get; set; }
    /// <summary>True once the backup copy was written AND re-hashed to match.</summary>
    public bool BackupVerified { get; set; }
}

public enum ChangeKind
{
    Unchanged,
    /// <summary>A real settings change: content differs even ignoring bookkeeping.</summary>
    Modified,
    /// <summary>
    /// Only per-launch bookkeeping differs (timestamps, run counters). Age of Empires IV
    /// does this on every single launch, replay or not, so it is not damage.
    /// </summary>
    BookkeepingOnly,
    Created,
    Removed,
}

public sealed class FileChange
{
    public string RelativePath { get; set; } = "";
    public ChangeKind Kind { get; set; }
    public string? BeforeSha256 { get; set; }
    public string? AfterSha256 { get; set; }
    public long? BeforeSizeBytes { get; set; }
    public long? AfterSizeBytes { get; set; }

    public override string ToString() => Kind switch
    {
        ChangeKind.Modified => $"Modified        {RelativePath} ({BeforeSizeBytes} -> {AfterSizeBytes} bytes)",
        ChangeKind.BookkeepingOnly => $"Bookkeeping     {RelativePath} (timestamps/counters only)",
        _ => $"{Kind,-15} {RelativePath}",
    };
}

public enum RestoreAction { Overwritten, Recreated, Quarantined, SkippedUnchanged, Failed }

public sealed class RestoreResult
{
    public string RelativePath { get; set; } = "";
    public RestoreAction Action { get; set; }
    public bool Verified { get; set; }
    public string? Detail { get; set; }

    public override string ToString() =>
        $"{Action,-16} {RelativePath}{(Verified ? " [verified]" : "")}{(Detail is null ? "" : $" - {Detail}")}";
}
