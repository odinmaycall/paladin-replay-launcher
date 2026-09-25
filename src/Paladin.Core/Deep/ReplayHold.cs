using Paladin.Core.Logging;

namespace Paladin.Core.Deep;

/// <summary>
/// §888 — THE REPLAY'S BYTES, TAKEN WHILE THEY ARE STILL THERE.
///
/// THE BUG THIS EXISTS FOR, found on the first real capture made with §879 installed. The console said
/// everything it was supposed to say and then contradicted itself:
///
///   [ok]   Sent. Paladin has 9,390 readings for this game.
///          Keeping this game's replay with Paladin, so the build order stays readable ...
///   [warn] The capture is safe, but this game's replay could not be kept (the replay could not be
///          read (Could not find file '...\playback\AgeIV_Replay_253128100')).
///
/// §879 uploaded FROM THE PATH, after `SessionRunner.RunAsync` returned — and the comment there claimed
/// that was "the last moment it is certainly here". It was the first moment it is certainly GONE. The
/// launcher places the replay into `playback\` itself, owns it (`PreparedReplayOwnedByLauncher`), and
/// removes it again in `CleanUp` at the end of that same call, because leaving files behind in the
/// user's game folder is the one thing Paladin Shield promises not to do. So the guard passed, the path
/// was right, and the file had been deleted by the very run that wanted it.
///
/// A PATH IS NOT EVIDENCE; BYTES ARE. So the read is moved to the one window where the file is neither
/// locked nor deleted — after the game exits, before the Shield compares and restores — and what is
/// carried out of that window is the finished wire body, not a promise to read one later.
///
/// IT COMPRESSES IMMEDIATELY, for two reasons. Gzip is what goes on the wire and what Paladin's bank
/// stores (`.rec.gz`), so compressing at the moment of reading means the size check happens while
/// something can still be done about it; and it is what makes holding the replay in memory cheap —
/// a median 289 KiB across 3,408 banked replays instead of the raw file.
///
/// FAILING HERE IS STILL NOT FATAL. The hold records why it is empty and the capture carries on: the
/// package reports `partial`, and §884's retry completes the game later without replaying anything.
/// </summary>
public sealed class ReplayHold
{
    private ReplayHold(string path, byte[]? body, int rawBytes, string? error)
    {
        Path = path;
        Body = body;
        RawBytes = rawBytes;
        Error = error;
    }

    /// <summary>Where it was read from, kept for the log rather than for a second read.</summary>
    public string Path { get; }

    /// <summary>The gzipped body, ready for the wire, or null when the read failed.</summary>
    public byte[]? Body { get; }

    /// <summary>The size before compression, which is what the console reports.</summary>
    public int RawBytes { get; }

    /// <summary>Why there are no bytes, phrased for the console. Null on success.</summary>
    public string? Error { get; }

    public bool Ok => Body is not null;

    /// <summary>
    /// Read and compress now.
    ///
    /// Synchronous on purpose: this is called from inside the Shield's finish, between the game exiting
    /// and the restore, and that sequence is not something to make await on a network-shaped API. A
    /// local read of a few hundred kilobytes costs milliseconds; the upload happens later, outside.
    ///
    /// Every failure is caught and described, because NOTHING this returns may endanger a restore.
    /// </summary>
    public static ReplayHold From(string path, PaladinLog? log = null)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            var body = ReplayUploader.Compress(raw);
            log?.Info($"Held {raw.Length} bytes of replay for retention ({body.Length} on the wire) from {path}");
            return new ReplayHold(path, body, raw.Length, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                     or ArgumentException or NotSupportedException)
        {
            log?.Warn($"Could not hold the replay for retention: {ex.Message}");
            return new ReplayHold(path, null, 0, $"the replay could not be read ({ex.Message})");
        }
    }
}
