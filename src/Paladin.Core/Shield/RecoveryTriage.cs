using Paladin.Core.Model;

namespace Paladin.Core.Shield;

/// <summary>
/// The rule that separates "the replay broke this" from "the user changed this later".
///
/// It lives in Paladin.Core, away from any Windows API, because it is the single most
/// dangerous decision the launcher makes: get it wrong in one direction and a crashed
/// session leaves the user stuck with dev-mode settings; get it wrong in the other and
/// Paladin silently reverts settings the user deliberately changed days afterwards.
/// </summary>
public static class RecoveryTriage
{
    /// <summary>
    /// How long after a session's last heartbeat a changed file is still assumed to be
    /// the replay's doing. Beyond this, a later deliberate edit is the likelier
    /// explanation and the file is left alone.
    /// </summary>
    public static readonly TimeSpan DefaultLaterEditGrace = TimeSpan.FromHours(6);

    /// <summary>
    /// Actionable: the replay's doing, restore them. Deferred: changed long after the
    /// session, leave them. Bookkeeping: timestamps and run counters the game rewrites
    /// on every launch — never damage, never restored, reported so the user sees why
    /// nothing happens.
    /// </summary>
    public sealed record Result(List<FileChange> Actionable, List<FileChange> Deferred, List<FileChange> Bookkeeping);

    public static Result Triage(
        IReadOnlyList<FileChange> changes,
        IReadOnlyList<FileSnapshot> current,
        DateTime sessionLastHeartbeatUtc,
        TimeSpan? laterEditGrace = null)
    {
        var currentByPath = current.ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase);
        var cutoff = sessionLastHeartbeatUtc + (laterEditGrace ?? DefaultLaterEditGrace);

        var actionable = new List<FileChange>();
        var deferred = new List<FileChange>();
        var bookkeeping = new List<FileChange>();

        foreach (var change in changes)
        {
            if (change.Kind == ChangeKind.Unchanged) continue;
            if (change.Kind == ChangeKind.BookkeepingOnly)
            {
                bookkeeping.Add(change);
                continue;
            }

            // A file that is gone now has no timestamp to judge, so it is always
            // actionable — a deleted settings file is exactly what recovery is for.
            if (currentByPath.TryGetValue(change.RelativePath, out var now) && now.ModifiedUtc > cutoff)
                deferred.Add(change);
            else
                actionable.Add(change);
        }

        return new Result(actionable, deferred, bookkeeping);
    }
}
