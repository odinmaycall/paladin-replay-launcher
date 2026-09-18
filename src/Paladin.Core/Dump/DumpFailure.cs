namespace Paladin.Core.Dump;

/// <summary>
/// The exit codes of the dump failures (§717 §2.5), continuing the launcher's own
/// table (SessionRunner.cs ExitCodes, which reserves 10-18 for exactly these). They
/// live in Core so the texts, the codes and the tests sit together; the launcher's
/// ExitCodes re-exports them.
/// </summary>
public static class DumpExitCodes
{
    /// <summary>F1: the replay never started (a custom map the game does not have).</summary>
    public const int ReplayNeverStarted = 10;
    /// <summary>F2: the developer console never answered.</summary>
    public const int ConsoleNeverOpened = 11;
    /// <summary>F3: the game's script functions are unavailable — it is not signed in.</summary>
    public const int NotSignedIn = 12;
    /// <summary>F4: a ladder definition was dropped.</summary>
    public const int DefinitionDropped = 13;
    /// <summary>F5: fewer rows were printed than the map has objects.</summary>
    public const int Incomplete = 14;
    /// <summary>F6: the game closed itself on a Scar error.</summary>
    public const int FatalScarError = 15;
    /// <summary>F7: another window took the focus while the map was being read.</summary>
    public const int FocusLost = 16;
    /// <summary>F10: Paladin refused the map.</summary>
    public const int UploadRejected = 17;
    /// <summary>F8: a game process was already running.</summary>
    public const int GameAlreadyRunning = 18;

    /// <summary>
    /// Not in the design's table: the evidence builder refused to keep a line (§3.2
    /// DumpEvidence). It cannot happen through the selectors, which is why it is the
    /// launcher's own "something went wrong" code rather than a dump code of its own.
    /// </summary>
    public const int EvidenceRefused = 9;
}

/// <summary>
/// One typed failure of a dump run (§717 §2.5): the table's code, the exact sentence
/// the console prints after "[fail] ", and the process exit code. Every run that fails
/// still closes the game and restores the settings; what was printed is still kept.
///
/// <paramref name="Detail"/> never reaches the console; it goes to dump.json and the
/// log, because it is where the machine-readable "why" belongs (which marker was
/// waited for, which chunk, what the injector said).
/// </summary>
public sealed record DumpFailure(string Code, string Text, int ExitCode, string? Detail = null)
{
    public override string ToString() => Detail is null ? $"{Code}: {Text}" : $"{Code}: {Text} ({Detail})";
}

/// <summary>The §717 §2.5 table, one factory a row. The texts are the design's, word for word.</summary>
public static class DumpFailures
{
    /// <summary>F1 — no "GAME -- Starting mission:" within the mission timeout; 15 of the night's 24 failures, all custom tournament maps.</summary>
    public static DumpFailure ReplayNeverStarted(string? detail = null) => new(
        "F1",
        "The replay did not start within 3 minutes. Tournament games on custom maps need that map's mod in the game; this game cannot be dumped on this PC yet. Nothing was sent.",
        DumpExitCodes.ReplayNeverStarted, detail);

    /// <summary>F2 — no PALADIN3_HELLO after two tries on each chord.</summary>
    public static DumpFailure ConsoleNeverOpened(string? detail = null) => new(
        "F2",
        "The game's developer console did not open (Alt+Shift+` and Ctrl+Shift+` were tried). Nothing was sent. If the key left of 1 on your keyboard is not the backtick, tell us which key it is.",
        DumpExitCodes.ConsoleNeverOpened, detail);

    /// <summary>F3 — the ENV count is not a number, or a fatal names a nil global: the game's sign-in has lapsed.</summary>
    public static DumpFailure NotSignedIn(string? detail = null) => new(
        "F3",
        "Age of Empires IV is not signed in, so its script functions are unavailable. Start the game normally once and sign in, then try again.",
        DumpExitCodes.NotSignedIn, detail);

    /// <summary>F4 — no PALADIN2_DEF sentinel, or a "|nil" in it.</summary>
    public static DumpFailure DefinitionDropped(string? detail = null) => new(
        "F4",
        "A dump function was not defined (the console dropped a line). Nothing was sent. Try again.",
        DumpExitCodes.DefinitionDropped, detail);

    /// <summary>F5 — rows &lt; the ENV count after the allowed retries. The counts and the folder are part of the sentence.</summary>
    public static DumpFailure Incomplete(int rows, int env, string keptFolder, string? detail = null) => new(
        "F5",
        $"Printed {rows:N0} of {env:N0} objects; Paladin needs all of them. Nothing was sent. The rows are kept at {keptFolder}. Try again.",
        DumpExitCodes.Incomplete, detail);

    /// <summary>F6 — "GameObj::OnFatalScarError" or "*FATAL SCAR ERROR": the game closes itself 1-5 s later.</summary>
    public static DumpFailure FatalScarError(string? detail = null) => new(
        "F6",
        "The game closed itself: a console line collided with the game's own text (rare). Nothing is damaged; your settings are checked below. Try again.",
        DumpExitCodes.FatalScarError, detail);

    /// <summary>F7 — the game did not hold the foreground before or after a line. Never followed by a re-send (§3.6 rule 3).</summary>
    public static DumpFailure FocusLost(string? detail = null) => new(
        "F7",
        "Another window took the focus while the map was being read. Stopped rather than retype a half-typed line. Try again and leave the game in front.",
        DumpExitCodes.FocusLost, detail);

    /// <summary>F8 — a game process exists before the launch; a dump has to start the game itself.</summary>
    public static DumpFailure GameAlreadyRunning(string? detail = null) => new(
        "F8",
        "Close Age of Empires IV first; a dump has to start the game itself.",
        DumpExitCodes.GameAlreadyRunning, detail);

    /// <summary>F10 — the Worker's 4xx, in plain words (<see cref="DumpUploadResponse.PlainWords"/>).</summary>
    public static DumpFailure UploadRejected(string plainWords, string? detail = null) => new(
        "F10",
        $"Paladin did not accept the map: {plainWords}.",
        DumpExitCodes.UploadRejected, detail);

    /// <summary>
    /// Not in the design's table: the game started, but the log it writes never appeared
    /// where Paladin watches. That is not F1 — F1 blames a custom map's missing mod and
    /// names a three-minute wait — it is a redirected Documents folder, or a LogFiles
    /// folder this account cannot enumerate. F1's exit code, because to a queue both mean
    /// "this game cannot be dumped on this PC"; the sentence is its own.
    /// </summary>
    public static DumpFailure GameLogNotFound(string logFilesFolder, string? detail = null) => new(
        "F14",
        $"Age of Empires IV started, but the log it writes never appeared in {logFilesFolder}. Nothing was sent. If your Documents folder has been moved, set Aoe4DocumentsPathOverride in config.json.",
        DumpExitCodes.ReplayNeverStarted, detail);

    /// <summary>
    /// The evidence builder's own refusal (§3.2): a line matching one of the forms that
    /// must never leave the machine would have been kept. Nothing is sent and the run ends.
    /// The form is named; the line never is.
    /// </summary>
    public static DumpFailure EvidenceRefused(string form) => new(
        "F13",
        $"Stopped before sending: a line of the form '{form}' would have been included, and that never leaves this PC. Nothing was sent.",
        DumpExitCodes.EvidenceRefused, form);
}
