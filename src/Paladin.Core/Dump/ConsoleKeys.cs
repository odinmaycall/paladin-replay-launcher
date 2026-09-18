using Paladin.Core.Config;

namespace Paladin.Core.Dump;

/// <summary>The two chords that can open the developer console (§717 §3.4). Alt+Shift opened it every time in the kit; Ctrl+Shift never had to.</summary>
public enum ConsoleChord
{
    /// <summary>Alt+Shift + the key left of 1 (scan code 0x29): Microsoft's Script Debugging page.</summary>
    AltShift,
    /// <summary>Ctrl+Shift + the same key: the community README's chord, tried second.</summary>
    CtrlShift,
}

public static class ConsoleChords
{
    /// <summary>The order they are tried in: altshift, then ctrlshift (launch-and-dump.ps1:208-219).</summary>
    public static readonly IReadOnlyList<ConsoleChord> InOrder = new[] { ConsoleChord.AltShift, ConsoleChord.CtrlShift };

    /// <summary>The name the envelope carries: "altshift" or "ctrlshift".</summary>
    public static string Name(ConsoleChord chord) => chord == ConsoleChord.AltShift ? "altshift" : "ctrlshift";

    /// <summary>The keys as the F2 text names them.</summary>
    public static string KeysText(ConsoleChord chord) => chord == ConsoleChord.AltShift ? "Alt+Shift+`" : "Ctrl+Shift+`";
}

/// <summary>
/// What became of one key sequence. The three ways of not being <see cref="Delivered"/>
/// are kept apart because the record and the log want to say which happened; to the
/// caller they all mean the same thing, which is §717 §3.6 rule 3: the line is never
/// re-sent.
/// </summary>
public enum KeyDelivery
{
    /// <summary>Every event was sent with the game's window in front, and it was still in front afterwards.</summary>
    Delivered,
    /// <summary>The game did not hold the foreground and one re-raise did not give it back: NOTHING was sent.</summary>
    NotSent,
    /// <summary>The events were sent and the game had lost the foreground by the end: the console's input box may hold half a line.</summary>
    LostAfter,
    /// <summary>The platform refused the send (SendInput inserted fewer events than asked, or the clipboard could not be opened at all).</summary>
    Failed,
}

/// <param name="Reraised">The game was not in front and one BringToFront put it there before the keys went in.</param>
public sealed record KeySendResult(KeyDelivery Delivery, string? Detail = null, bool Reraised = false)
{
    public bool Sent => Delivery == KeyDelivery.Delivered;

    /// <summary>The keys went in and the window changed under them: F7's "rather than retype a half-typed line" case.</summary>
    public bool MayHaveTyped => Delivery == KeyDelivery.LostAfter;

    public static KeySendResult Ok(bool reraised = false) => new(KeyDelivery.Delivered, null, reraised);
    public static KeySendResult NotSent(string detail) => new(KeyDelivery.NotSent, detail);
    public static KeySendResult LostAfter(string detail) => new(KeyDelivery.LostAfter, detail);
    public static KeySendResult Failed(string detail) => new(KeyDelivery.Failed, detail);
}

/// <summary>
/// The keyboard as the console driver sees it (§717 §3.2 KeyboardInjector). Every
/// member checks that the game's window holds the foreground first, re-raises it
/// once if not, and sends nothing when it still is not in front — the caller decides
/// what that means (§3.6 rule 3: never re-send after a focus loss). Synchronous:
/// SendInput and the clipboard are. The production implementation is the launcher's
/// KeyboardInjector; the tests fake it.
/// </summary>
public interface IConsoleKeys
{
    /// <summary>"paste" or "type": how <see cref="PasteLine"/> last delivered text (D5: paste first; typing only when the clipboard cannot be opened).</summary>
    string InputMethod { get; }

    /// <summary>The console toggle: the chord's modifiers plus the key left of 1, by scan code.</summary>
    KeySendResult Chord(ConsoleChord chord);

    /// <summary>Puts the text on the clipboard and sends Ctrl+V. The line is delivered whole or not at all (§3.6 rule 2). No Return.</summary>
    KeySendResult PasteLine(string text);

    /// <summary>Sends Return.</summary>
    KeySendResult Enter();

    /// <summary>GetForegroundWindow() belongs to the game's process, right now. No re-raise.</summary>
    bool IsGameInFront();
}

/// <summary>
/// The game's per-session log as the driver reads it (§717 §3.2 GameLogWatcher):
/// complete lines so far, a wait for a line at or after an index, and the two files'
/// discovery. Indexing from a point lets a marker be demanded AFTER the line that
/// should print it, so a HELLO printed by an earlier try never answers a later one.
/// </summary>
public interface IDumpLogSource
{
    /// <summary>Waits for the game's session folder and its warnings.&lt;date&gt;.txt to exist; false on timeout.</summary>
    Task<bool> WaitForSessionLogAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Where the session log was found, for the record; null until it is.</summary>
    string? SessionLogPath { get; }

    /// <summary>
    /// The LogFiles folder that was watched, already safe to print (no user name): the
    /// one thing worth saying to someone whose game started but whose log never turned up.
    /// </summary>
    string LogFilesDisplayPath { get; }

    /// <summary>Every complete line read so far, after the last <see cref="Refresh"/>.</summary>
    IReadOnlyList<string> Lines { get; }

    /// <summary>Those same lines as one text: what <see cref="DumpEvidence"/> is built from.</summary>
    string TextSoFar { get; }

    /// <summary>Reads what the game appended since the last call. Returns the line count.</summary>
    int Refresh();

    /// <summary>Polls until a line at index ≥ <paramref name="fromIndex"/> satisfies the predicate. The index found, or -1 on timeout.</summary>
    Task<int> WaitForLineAsync(int fromIndex, Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct);

    /// <summary>The first <see cref="DumpEvidence.TopLevelLinesRead"/> lines of the top-level warnings.log, or null when it cannot be read.</summary>
    string? ReadTopLevelHead();
}

/// <summary>The console lines of §717 §2.2, as the run reports them. The launcher adapts IShieldUi; the tests record.</summary>
public interface IDumpReporter
{
    void Ok(string message);
    void Pending(string message);
    void Warn(string message);
    void Fail(string message);
    void Note(string message);
}

/// <summary>
/// Where a run's own files go (§717 §3.2 DumpRecord): the session folder's dump.json,
/// the evidence text scripts/world-dump/run.mjs reads, and the envelope a later
/// --dump-upload re-sends. An interface so the ordering rule — the evidence is on
/// disk before the game is ended — is testable without a game and without a disk.
/// </summary>
public interface IDumpStore
{
    /// <summary>Writes dump.json, temp+replace, after every phase.</summary>
    void SaveRecord(DumpRecord record);

    /// <summary>Writes the header+rows text; returns its path.</summary>
    string SaveEvidence(string text);

    /// <summary>Writes the upload envelope beside it; returns its path.</summary>
    string SaveEnvelope(DumpEnvelope envelope);

    /// <summary>The folder both files are in, already safe to print (no user name).</summary>
    string DisplayFolder { get; }
}

/// <summary>The two requests a dump makes to Paladin, as the pure run sees them. <see cref="DumpUploader"/> is the implementation.</summary>
public interface IDumpUploadService
{
    /// <summary>
    /// The address the rows are actually sent to ("paladin.odinmaycall.com",
    /// "localhost:8787"), for the console lines that name it. It comes from the
    /// configured base URL, so what the user is told and where the POST goes cannot
    /// drift apart.
    /// </summary>
    string Host { get; }

    Task<DumpPreflight> CheckAsync(long gameId, CancellationToken ct);
    Task<DumpUploadResult> UploadAsync(DumpEnvelope envelope, CancellationToken ct);
}

/// <summary>
/// The pauses and waits of the run (§717 §3.2 LauncherConfig knobs), read from
/// config.json once so the driver never sees the config. Milliseconds are the kit's
/// measured pauses (console-dump.ahk 60, 92, 94); seconds are its waits.
/// </summary>
public sealed class DumpPacing
{
    public int ChordSettleMs { get; init; } = 1200;
    public int AfterPasteMs { get; init; } = 800;
    public int AfterEnterMs { get; init; } = 600;
    public int HudSettleMs { get; init; } = 3000;
    public int HelloWaitSeconds { get; init; } = 6;
    public int DefWaitSeconds { get; init; } = 8;
    public int ChunkWaitSeconds { get; init; } = 30;
    public int ChunkSize { get; init; } = DumpLadder.ChunkSize;
    public int MissionTimeoutSeconds { get; init; } = 180;
    public int CloseGraceSeconds { get; init; } = 15;
    public bool EndProcess { get; init; } = true;

    /// <summary>After a failed HELLO try, the pause after toggling the console back (launch-and-dump.ps1:216).</summary>
    public const int ToggleBackMs = 2000;
    /// <summary>The squad call's SQ_DONE wait (launch-and-dump.ps1:254).</summary>
    public const int SquadDoneWaitSeconds = 60;
    /// <summary>The session folder (≤ 60 s) and its file (≤ 30 s) after the process appears (launch-and-dump.ps1:153-165).</summary>
    public const int SessionLogWaitSeconds = 90;
    /// <summary>The ENV line follows HELLO in the same paste; this is how long it gets when it is not there yet.</summary>
    public const int EnvWaitSeconds = 2;
    /// <summary>The BEGIN line's wait; the kit gave it the ladder's 8 s (launch-and-dump.ps1:240).</summary>
    public const int BeginWaitSeconds = 8;

    public static DumpPacing From(LauncherConfig config) => new()
    {
        ChordSettleMs = config.DumpChordSettleMs,
        AfterPasteMs = config.DumpAfterPasteMs,
        AfterEnterMs = config.DumpAfterEnterMs,
        HudSettleMs = config.DumpHudSettleMs,
        HelloWaitSeconds = config.DumpHelloWaitSeconds,
        DefWaitSeconds = config.DumpDefWaitSeconds,
        ChunkWaitSeconds = config.DumpChunkWaitSeconds,
        ChunkSize = config.DumpChunkSize,
        MissionTimeoutSeconds = config.DumpMissionTimeoutSeconds,
        CloseGraceSeconds = config.DumpCloseGraceSeconds,
        EndProcess = config.DumpEndProcess,
    };
}

public enum ExitAction
{
    /// <summary>Today's wait: the game closes when the user closes it (attended mode, pass F).</summary>
    WaitForUser,
    /// <summary>D1 amended: the launcher ends the game process itself after <see cref="ExitPolicy.Grace"/>.</summary>
    EndProcess,
}

/// <summary>What the WhileRunning hook asks of the session runner once its work is done (§717 §3.2).</summary>
public sealed record ExitPolicy(ExitAction Action, TimeSpan Grace)
{
    public static readonly ExitPolicy WaitForUser = new(ExitAction.WaitForUser, TimeSpan.Zero);

    /// <summary>End the process; wait <paramref name="grace"/> first for the game to close by itself (it does after a fatal error).</summary>
    public static ExitPolicy EndProcess(TimeSpan grace) => new(ExitAction.EndProcess, grace);
}
