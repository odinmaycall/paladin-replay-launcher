using Paladin.Core.Dump;

namespace Paladin.Core.Deep;

/// <summary>
/// §867 — everything a Deep capture says out loud.
///
/// THE USER SEES "DEEP CAPTURE" AND NOTHING ELSE. Not "dump", not "Standard", not "sampler", not
/// "SimRate" — the owner's instruction is that the normal user should not meet implementation
/// terminology, and there is only one capture mode now, so there is nothing to distinguish it from.
/// The words that survive are the ones that describe what is happening to the person's afternoon: the
/// replay is playing, it will take a few minutes, leave the machine alone.
/// </summary>
public static class DeepConsoleText
{
    public const string Title = "deep capture";

    public static string GameLine(long gameId, string? map) =>
        map is null ? $"game {gameId}" : $"game {gameId} · {map}";

    /// <summary>What is about to happen, before the countdown. No jargon, one promise per line.</summary>
    public static IReadOnlyList<string> WhatWillHappen(bool upload, string host) => new[]
    {
        "Deep Capture plays this replay through Age of Empires IV and reads what every villager is doing.",
        "It takes a few minutes and needs the machine to itself: the game must keep the keyboard.",
        upload
            ? $"When it finishes, the result is sent to {host} and the game's build order opens on Paladin."
            : "The result stays on this PC; nothing is sent.",
    };

    public static IReadOnlyList<string> HandsOff() => new[]
    {
        "Hands off from here: Paladin is typing into the game's own console.",
        "Moving the mouse is fine. Clicking away from the game is not — it would take the keyboard.",
    };

    public const string ReplayStarted = "The replay is playing.";

    public static string ConsoleOpen(int entities) => $"Connected to the game ({entities:N0} things on the map).";

    public const string Frozen = "Paused the replay to set up — this costs no game time.";

    public static string Installing(int lines) => $"Setting up ({lines} steps)…";

    public static string Installed(int attempt) =>
        attempt == 1 ? "Set up and checked." : $"Set up and checked (after {attempt - 1} repair{(attempt == 2 ? "" : "s")}).";

    /// <summary>
    /// The repair line. It names what was lost because the person watching deserves to know the
    /// launcher noticed something rather than silently pausing — but it says "steps", not helper names.
    /// </summary>
    public static string Repairing(IReadOnlyList<string> missing, int attempt, int of) =>
        $"The game dropped {missing.Count} setup step{(missing.Count == 1 ? "" : "s")}; sending them again (try {attempt} of {of}).";

    public static string Running(int cadence, int rate) =>
        $"Capturing every {cadence} seconds of game time, at {rate / 8}× speed.";

    public static string FirstSample(int at) => $"Reading from {Clock(at)} — the whole build order is covered.";

    public static string Progress(int at, int window) => $"Captured to {Clock(at)} of {Clock(window)}…";

    /// <summary>
    /// §876 — said once, before the launch, when Paladin reports this game ended before 15:00. Without
    /// it the reader watches a progress line count towards a clock their game never had.
    /// </summary>
    public static string ShortGame(int window) =>
        $"This game ended before 15:00, so the capture runs to {Clock(window)} — the whole match.";

    public static string Captured(int samples, int first, int last) =>
        $"Captured {samples:N0} readings from {Clock(first)} to {Clock(last)}.";

    public static string Sending(int rawBytes, int gzipBytes, string host) =>
        $"Sending to {host} ({Kb(gzipBytes)}, compressed from {Kb(rawBytes)})…";

    public static string Accepted(int samples) => $"Sent. Paladin has {samples:N0} readings for this game.";

    public const string SomeoneElseWasFirst = "Paladin already had a capture for this game; nothing was stored.";

    public const string ReloadThePage = "Reload the game's page on Paladin to see its build order.";

    public static string KeptLocally(int bytes, string folder) => $"Kept {Kb(bytes)} here: {folder}";

    public static string SendLaterWarn(string folder) =>
        $"The capture is safe on disk ({folder}) but Paladin could not be reached. Nothing was lost.";

    private static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    private static string Kb(int bytes) => bytes < 1024 ? $"{bytes} bytes" : $"{bytes / 1024.0:0.#} KB";
}

/// <summary>§867 — the ways a Deep capture stops, in the dump's own failure vocabulary.</summary>
public static class DeepFailures
{
    /// <summary>
    /// The console lost lines and kept losing them. This is the four-in-twenty failure the whole gate
    /// exists for; reaching it means even the repairs did not land, which is a machine or focus problem
    /// rather than bad luck.
    /// </summary>
    public static DumpFailure BootstrapLost(string detail) => new(
        "D1",
        "Deep Capture could not finish setting up inside the game. Nothing was sent.",
        DumpExitCodes.DefinitionDropped, detail);

    /// <summary>Installed, reported ready, and printed nothing. Distinct from a short capture.</summary>
    public static DumpFailure NoSamples(string detail) => new(
        "D2",
        "Deep Capture set up but the game never reported anything. Nothing was sent.",
        DumpExitCodes.Incomplete, detail);

    /// <summary>
    /// Short. Refused rather than sent, because an incomplete capture is not a better build order than
    /// no capture — it is a worse one with a gap, and the Worker refuses it at the door in any case.
    /// </summary>
    public static DumpFailure Incomplete(int reached, int window, string folder) => new(
        "D3",
        $"The replay stopped at {reached / 60}:{reached % 60:00} of the {window / 60}:{window % 60:00} Deep Capture needs. Nothing was sent.",
        DumpExitCodes.Incomplete, $"reached {reached}s of {window}s; the rows are at {folder}");
}
