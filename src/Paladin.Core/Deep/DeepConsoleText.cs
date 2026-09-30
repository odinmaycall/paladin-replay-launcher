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
    /// <summary>
    /// 926 — `opensPage` is false under --no-browser, and the promise is withdrawn rather than left standing.
    /// The line said the build order "opens on Paladin" unconditionally, which under the flag was simply untrue,
    /// and a console that mis-describes what it is about to do is how an unattended run gets misread.
    /// </summary>
    public static IReadOnlyList<string> WhatWillHappen(bool upload, string host, bool opensPage = true) => new[]
    {
        "Deep Capture plays this replay through Age of Empires IV and reads what every villager is doing.",
        "It takes a few minutes and needs the machine to itself: the game must keep the keyboard.",
        upload
            ? opensPage
                ? $"When it finishes, the result is sent to {host} and the game's build order opens on Paladin."
                : $"When it finishes, the result is sent to {host}. --no-browser: no page will be opened."
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

    /// <summary>
    /// 901 - the build order is complete and the map is not.
    ///
    /// The map is optional BY DESIGN: a dropped snapshot or terrain line may never void a capture.
    /// Until now that also meant it said nothing at all, so a reader whose map silently failed had no
    /// way to know. This is the whole point of the reported category.
    /// </summary>
    public static string MapNotRead(IReadOnlyList<string> missing) =>
        $"Your build order is complete. The opening map was not read this time — the game dropped "
        + $"{missing.Count} optional setup step{(missing.Count == 1 ? "" : "s")}.";

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

    /// <summary>
    /// §879 — the replay going up after the capture. Said in the reader's terms: what it is for, not
    /// what it is. "Keeping the replay" is true and useful; "POSTing a gzipped .rec" is neither.
    /// </summary>
    public static string RetainingReplay() => "Keeping this game's replay with Paladin, so the build order stays readable after Microsoft drops it…";

    public static string ReplayRetained(int wireBytes, int rawBytes, bool parsed) =>
        parsed
            ? $"Replay kept ({Kb(wireBytes)}, from {Kb(rawBytes)}) and read. This game's build order is complete."
            : $"Replay kept ({Kb(wireBytes)}, from {Kb(rawBytes)}). Paladin will read it shortly.";

    /// <summary>
    /// §879 — a retention that failed is a WARNING, never an error: the capture already landed and was
    /// accepted, and the replay can be sent again later without replaying the game.
    /// </summary>
    public static string ReplayNotRetained(string? why) =>
        $"The capture is safe, but this game's replay could not be kept ({why ?? "unknown"}). Its build order will show as partial until it is.";

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

/// <summary>
/// 926 — WHETHER A LANDED CAPTURE OPENS THE BUILD-ORDER PAGE.
///
/// §869 opened it on every successful capture, which is right for the one capture a reader starts by
/// hand and wrong for a queue: the owner's Deep queue runs 561 games unattended, and 561 browser tabs
/// is not a side effect, it is a machine that has to be rescued in the morning. Worse, a window taking
/// the foreground is failure code 16 (F7 FocusLost), so the page opened for one game can cost the next
/// one.
///
/// THE THIRD OUTCOME IS THE POINT. --no-browser does not mean silence: the capture landed and the
/// reader still needs to know where it went, so the URL is PRINTED instead of opened. A suppressed
/// page that said nothing would be indistinguishable from a page that failed to open.
///
/// A PURE DECISION HERE, like DumpCountdown.ShouldWait, because the Launcher project is not reachable
/// from the tests and a flag whose behaviour cannot be tested is a flag nobody can trust.
/// </summary>
public static class DeepResultPage
{
    /// <summary>Total on the three facts that decide it, so there is no fourth unstated case.</summary>
    public static DeepResultPageAction ActionFor(bool captureOk, bool dryRun, bool noBrowser) =>
        !captureOk || dryRun ? DeepResultPageAction.None
        : noBrowser ? DeepResultPageAction.PrintOnly
        : DeepResultPageAction.Open;

    /// <summary>What the console says instead of opening a window. It names the flag, so the reader knows the page was withheld deliberately rather than broken.</summary>
    public static string NotOpening(string url) => $"--no-browser: the build order is at {url}";
}

/// <summary>926 — the three things that can happen to the build-order page when a run ends.</summary>
public enum DeepResultPageAction
{
    /// <summary>Nothing landed, or nothing ran: there is no build order to point at.</summary>
    None,
    /// <summary>The reader's own capture: take them to it, and carry the announcement along.</summary>
    Open,
    /// <summary>--no-browser: say where it is and open nothing.</summary>
    PrintOnly,
}
