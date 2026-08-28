namespace Paladin.Core.Replay;

/// <summary>What the user (or a paladin:// link) asked for, before any provider has looked at it.</summary>
public sealed class ReplayRequest
{
    /// <summary>Provider id: "local", "url", or a future archive id such as "aoe4replays".</summary>
    public required string Kind { get; init; }
    /// <summary>A file path, an http(s) URL, or an archive-specific id.</summary>
    public required string Value { get; init; }
    /// <summary>Optional display name supplied by the caller (e.g. from a paladin:// link).</summary>
    public string? SuggestedName { get; init; }

    /// <summary>
    /// Further candidates to try, in order, if <see cref="Value"/> fails.
    ///
    /// This exists because Microsoft's replay endpoint is scoped per PARTICIPANT, not per
    /// match: the same game returns 404 for one player's profileId and the real replay for
    /// the other's. The endpoint rejects HEAD (405), so a website cannot cheaply discover
    /// which one works — probing would mean downloading the whole replay in the browser
    /// just to choose a link. Handing the launcher both and letting it try each is the
    /// cheaper and more honest arrangement.
    /// </summary>
    public IReadOnlyList<string> Fallbacks { get; init; } = Array.Empty<string>();

    /// <summary>Every candidate in priority order, primary first.</summary>
    public IEnumerable<string> AllCandidates()
    {
        yield return Value;
        foreach (var fallback in Fallbacks) yield return fallback;
    }

    public override string ToString() =>
        Fallbacks.Count == 0 ? $"{Kind}:{Value}" : $"{Kind}:{Value} (+{Fallbacks.Count} fallback)";
}

/// <summary>A replay file sitting on local disk, ready to be placed for AoE4.</summary>
public sealed class AcquiredReplay
{
    public required string LocalPath { get; init; }
    /// <summary>Base name to use when placing it, without any directory part.</summary>
    public required string SuggestedFileName { get; init; }
    public required long SizeBytes { get; init; }
    /// <summary>True when the provider itself created this file and cleanup may delete it.</summary>
    public required bool IsTemporary { get; init; }
}

/// <summary>
/// The only thing the launcher core knows about replay acquisition. Adding
/// aoe4replays.gg or an official Relic API later means adding one implementation
/// of this interface and registering it — no change to the session pipeline.
/// </summary>
public interface IReplayProvider
{
    /// <summary>Stable id matching <see cref="ReplayRequest.Kind"/>.</summary>
    string Id { get; }

    /// <summary>Human-readable name for logs and UI.</summary>
    string DisplayName { get; }

    bool CanHandle(ReplayRequest request);

    /// <summary>
    /// Produce a local replay file. Implementations must validate what they produced
    /// and throw <see cref="ReplayAcquisitionException"/> on anything suspicious rather
    /// than returning a zero-byte or HTML-error-page "replay".
    /// </summary>
    Task<AcquiredReplay> AcquireAsync(ReplayRequest request, string workingDirectory, CancellationToken ct);
}

public sealed class ReplayAcquisitionException : Exception
{
    public ReplayAcquisitionException(string message) : base(message) { }
    public ReplayAcquisitionException(string message, Exception inner) : base(message, inner) { }
}
