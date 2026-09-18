using Paladin.Core.Replay;

namespace Paladin.Core.Dump;

/// <summary>
/// One "Dump this game" run as the launcher was asked for it: the game the rows are
/// sent under, the replay to play, and the switches (§717 §3.1). Built from a
/// paladin://dump link or from --dump on the command line; the upload target is never
/// part of it (that is config, so a link can never redirect a dump).
/// </summary>
/// <param name="GameId">The Paladin game id, digits, at most 15.</param>
/// <param name="Replay">Where the replay comes from — the same ReplayRequest a watch uses.</param>
/// <param name="Upload">False for --no-upload: the rows stay on this PC and nothing is asked of Paladin.</param>
/// <param name="Squads">--squads: also type the squad ladder (the owner's own queue).</param>
/// <param name="Force">--force: dump even when Paladin already has a world layer for the game.</param>
/// <param name="AssumeYes">--yes: skip the countdown (an unattended queue).</param>
/// <param name="ThenWatch">
/// The page's "Dump, then watch" (&amp;then=watch): after a successful dump, the replay is
/// launched again as an ordinary watch session, Shield and all. A second launch rather
/// than a continuation of the first, because the dump's game is ended as soon as the
/// evidence is safe (D1 amended) and a replay has nothing to save.
/// </param>
public sealed record DumpRequest(
    long GameId,
    ReplayRequest Replay,
    bool Upload = true,
    bool Squads = false,
    bool Force = false,
    bool AssumeYes = false,
    bool ThenWatch = false)
{
    /// <summary>The name the replay must be placed under for the game to accept it, and for RUN-OPTIONS to name this game.</summary>
    public string ExpectedReplayName => $"AgeIV_Replay_{GameId}";
}

/// <summary>The checks a dump makes that a watch does not (§717 §3.3 steps 1 and 2).</summary>
public static class DumpChecks
{
    /// <summary>
    /// The replay that was acquired must be this game's: the launcher names the placed
    /// file AgeIV_Replay_&lt;gameId&gt;, RUN-OPTIONS then carries that name, and the Worker
    /// refuses an envelope whose RUN-OPTIONS names another game (wrong_replay). Catching
    /// it here saves a two-minute run. A "_paladin" clash suffix still passes, as it does
    /// in run.mjs:53.
    /// </summary>
    public static bool NameMatchesGame(string? placedName, long gameId) =>
        placedName is not null && placedName.StartsWith($"AgeIV_Replay_{gameId}", StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal, in the console's words.</summary>
    public static string WrongReplayMessage(string placedName, long gameId) =>
        $"The replay arrived as '{placedName}' but the link named game {gameId}. Nothing was launched.";
}
