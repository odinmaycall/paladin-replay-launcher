using Paladin.Core.Dump;

namespace Paladin.Core.Deep;

/// <summary>
/// A MIRROR MATCHUP CANNOT BE TOLD APART FROM THE CAPTURE ALONE.
///
/// Both players' villagers carry the same blueprint name, so Paladin decides which owner is which player
/// by where each one STARTED, against the spawn points in the replay's own spatial sidecar. A game the
/// nightly has already read has that sidecar; a fresh one does not, and the Worker answers
/// `ambiguous_sides` because it holds nothing to settle it with.
///
/// The launcher already holds the replay's bytes at that moment. Sending them first, and then sending the
/// same capture again, is the whole fix: the Worker parses a retained replay on the spot, and the second
/// answer is decided.
/// </summary>
public static class MirrorRetry
{
    /// <summary>True only for the Worker's one refusal that a retained replay can cure.</summary>
    public static bool WantsReplayFirst(DumpUploadResult? upload) =>
        upload is { Accepted: false } && string.Equals(upload.Code, "ambiguous_sides", StringComparison.Ordinal);
}
