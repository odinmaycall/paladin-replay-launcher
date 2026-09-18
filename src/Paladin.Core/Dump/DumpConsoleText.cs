using System.Globalization;

namespace Paladin.Core.Dump;

/// <summary>
/// Every line "Dump this game" prints (§717 §2.2), as pure functions of what the run
/// knows. They live here, not in the runner, for two reasons: the wording is a promise
/// to the user (what is sent, and that it is hands-off) and a promise to the README,
/// and both are worth a test that reads the sentence.
/// </summary>
public static class DumpConsoleText
{
    /// <summary>The header's second line: "Game:    246737201" plus the map when the page named one.</summary>
    public static string GameLine(long gameId, string? map) =>
        map is { Length: > 0 } ? $"{gameId} ({map})" : gameId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The pre-flight's good news: nothing to overwrite, so the run may go ahead.</summary>
    public const string NoWorldLayerYet = "Paladin has no world layer for this game yet";

    /// <summary>
    /// Where the rows go when nothing has said otherwise. Every line that names an address
    /// takes the host as an argument, because DumpUploadBaseUrl can point somewhere else
    /// (§5.5 points it at localhost:8787 for `wrangler dev`) and the user must be told the
    /// address their rows are really sent to, not the one that was true when this was written.
    /// </summary>
    public const string DefaultHost = "paladin.odinmaycall.com";

    /// <summary>What the run is about to do and what leaves the machine — printed BEFORE the countdown, never after.</summary>
    public static IReadOnlyList<string> WhatWillHappen(bool upload, string host) => upload
        ? new[]
        {
            "This run starts the game in replay mode, prints the map's objects from the game's own",
            "console (about 60 s), sends those rows (about 150 KB of text, nothing else) to",
            $"{host}, closes the game and puts your settings back.",
        }
        : new[]
        {
            "This run starts the game in replay mode, prints the map's objects from the game's own",
            "console (about 60 s), keeps those rows on this PC (--no-upload: nothing is sent),",
            "closes the game and puts your settings back.",
        };

    /// <summary>D3's consent moment.</summary>
    public static string Countdown(int seconds) => $"Starting in {seconds} s — Enter to start now, Ctrl+C to stop.";

    /// <summary>The two lines printed once the game is up: the hands-off rule and the -dev prompt (§2.3).</summary>
    public static IReadOnlyList<string> HandsOff() => new[]
    {
        "Hands off for about two minutes: do not click or type, and leave the game in front.",
        "An \"Account Authentication\" message will appear — that is the game's replay mode. Leave it.",
    };

    public const string ReplayStarted = "Replay started";

    public static string ConsoleOpen(int entityCount) =>
        $"Console open — {entityCount:N0} objects on the map";

    public static string PrintingChunk(int index, int total) =>
        $"Printing the map: chunk {index} of {total} ...";

    public static string Printed(int rows, int env, int errors, double seconds) =>
        $"{rows:N0} of {env:N0} objects printed, {errors} errors, in {seconds:0} s";

    public static string Sending(long bytes, string host) =>
        $"Sending {Kb(bytes)} to {host} ...";

    /// <summary>The Worker's "stored" answer, with the checks it named (§4.3's `verified`).</summary>
    public static string Accepted(DumpUploadResponse? response)
    {
        var checks = response?.Verified is { Count: > 0 } list ? string.Join(", ", list.Select(Plain)) : null;
        return checks is null
            ? "Paladin accepted the map."
            : $"Paladin accepted the map: {checks} match its own data.";
    }

    /// <summary>The verified codes in the words §2.2 uses ("seed, town centres and 65 worked targets match its own data").</summary>
    private static string Plain(string code) => code switch
    {
        "run_options" => "the replay it played",
        "complete" => "every object",
        "first_id" => "the first object",
        "seed" => "seed",
        "map" => "map",
        "tc" => "town centres",
        "orders" => "worked targets",
        _ => code,
    };

    public const string ReloadThePage = "Reload the match page to see the World layer.";

    /// <summary>--no-upload, and the owner's own queue: where the rows are.</summary>
    public static string KeptLocally(long bytes, string folder) =>
        $"{Kb(bytes)} of rows kept at {folder} (nothing was sent).";

    /// <summary>F11: the send failed and can be repeated later without the game.</summary>
    public static string SendLaterWarn(string sessionId, string host) =>
        $"{host} could not be reached. The rows are kept; run `PaladinReplayLauncher.exe --dump-upload {sessionId}` later.";

    /// <summary>200 "already": someone else's verified dump holds the slot and this one was not stored.</summary>
    public const string SomeoneElseWasFirst = "Someone else's map for this game arrived first, so this one was not needed.";

    public const string ClosingTheGame = "Closing the game ...";

    public static string EndedTheGame(int graceSeconds) =>
        $"The game did not close by itself within {graceSeconds} s; ending it (a replay has nothing to save).";

    /// <summary>The clipboard note of D5: the run puts a line on the clipboard and puts the user's text back.</summary>
    public const string ClipboardRestored = "Your clipboard was used for the console lines and has been put back.";

    /// <summary>D5's honest limit: only text can be put back.</summary>
    public const string ClipboardImageLost = "Your clipboard held an image, which could not be put back; the last console line is on it instead.";

    /// <summary>The clipboard was empty when the run borrowed it, so the console line is taken off rather than left there.</summary>
    public const string ClipboardCleared = "Your clipboard was used for the console lines and has been emptied again (it was empty before).";

    /// <summary>Windows would not let the clipboard be given back. Never silent: the user is holding a Lua line.</summary>
    public const string ClipboardNotPutBack = "Your clipboard was used for the console lines and Windows would not let it be put back; a console line may still be on it.";

    /// <summary>"147 KB", "1.2 MB" — the size as the console says it.</summary>
    public static string Kb(long bytes) =>
        bytes >= 1024L * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
            : $"{Math.Max(1, (long)Math.Round(bytes / 1024.0)):N0} KB";
}

/// <summary>What the pre-flight GET decided, and the sentence that goes with it (§717 §3.3 step 0, D9).</summary>
/// <param name="Proceed">True: launch. False: stop before anything is launched.</param>
/// <param name="Message">The one line the console prints, whichever way it went.</param>
/// <param name="Failure">Set when the run stops, so the exit code is the design's.</param>
/// <param name="Warn">The message is a [warn], not an [ok]: the check itself did not answer.</param>
public sealed record DumpPreflightDecision(bool Proceed, string Message, DumpFailure? Failure = null, bool Warn = false)
{
    /// <summary>
    /// The statuses of §4.1 plus the amended "unknown": Paladin has no sidecar for this
    /// game, so a dump could be neither checked nor drawn and the run stops BEFORE the
    /// game is launched. "owner"/"user" stop too, and only those two are overridable
    /// with --force; an unreachable check is a warning and the run goes on.
    /// </summary>
    public static DumpPreflightDecision For(DumpPreflight preflight, bool force)
    {
        if (!preflight.Reachable || preflight.Status is null)
            return new DumpPreflightDecision(
                true,
                $"Paladin could not be asked whether this game already has a map ({preflight.Error ?? "no answer"}); going ahead.",
                Warn: true);

        var status = preflight.Status.Status;
        switch (status)
        {
            case "none":
                return new DumpPreflightDecision(true, DumpConsoleText.NoWorldLayerYet);

            case "owner":
            case "user":
            {
                var whose = status == "owner" ? "the owner's map" : "a map someone sent";
                if (force)
                    return new DumpPreflightDecision(true, $"This game already has {whose}; --force was given, so the run goes ahead.", Warn: true);
                return new DumpPreflightDecision(
                    false,
                    $"This game already has its world layer ({whose}). Nothing to do; --force dumps it anyway.",
                    DumpFailures.UploadRejected(DumpUploadResponse.PlainWords(status == "owner" ? "banked" : "already")));
            }

            case "unknown":
                return new DumpPreflightDecision(
                    false,
                    "Paladin has no map data for this game yet, so a dump could not be checked against anything. Nothing was started.",
                    DumpFailures.UploadRejected(DumpUploadResponse.PlainWords("no_sidecar")));

            default:
                return new DumpPreflightDecision(
                    false,
                    $"Paladin answered with a status this version does not understand ('{status ?? "none given"}'). Nothing was started.",
                    DumpFailures.UploadRejected("Paladin's answer was not understood"));
        }
    }
}

/// <summary>D3: the 5 s countdown before a dump launches anything.</summary>
public static class DumpCountdown
{
    public const int Seconds = 5;

    /// <summary>
    /// --yes skips it (the owner's queue runs unattended), and so does a console with no
    /// keyboard: waiting for Enter that can never come would hang a scripted run.
    /// </summary>
    public static bool ShouldWait(bool assumeYes, bool inputRedirected) => !assumeYes && !inputRedirected;
}
