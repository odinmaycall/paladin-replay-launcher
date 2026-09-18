using Paladin.Core.Logging;

namespace Paladin.Core.Dump;

/// <summary>What became of one delivered line.</summary>
public enum LineOutcome
{
    /// <summary>The line went in and its marker printed (or it has no marker and the console kept up).</summary>
    Landed,
    /// <summary>The line went in whole, the game held the foreground throughout, and the marker never came.</summary>
    MarkerMissing,
    /// <summary>The game did not hold the foreground before or after the line: the run stops (§3.6 rule 3).</summary>
    FocusLost,
    /// <summary>A fatal Scar error printed: the game closes itself within seconds.</summary>
    Fatal,
}

/// <param name="Index">The log line the marker was found at, or -1.</param>
public sealed record LineResult(LineOutcome Outcome, int Index = -1, string? Line = null, string? Detail = null, int Tries = 1)
{
    public bool Landed => Outcome == LineOutcome.Landed;
}

/// <summary>
/// The console script of §717 §3.3, driven entirely through <see cref="IConsoleKeys"/>
/// and <see cref="IDumpLogSource"/>: no window, no keyboard, no file. That is what makes
/// the whole sequence — the two chords, the ladder, the chunks, the retry rule — testable
/// against a scripted log.
///
/// The lines are DumpLadder's, in DumpLadder.Script's order, with ONE deliberate
/// difference: quit() is never typed. That is the owner's amended D1 — quit() is nil when
/// the game's sign-in has lapsed, and the Fatal Scar Error box that follows blocks the run.
/// The launcher ends the process itself once the evidence is saved.
///
/// The retry rule is §3.6, and it is the whole of it:
///   rule 2  one paste, then Return, with the foreground checked before and after;
///   rule 3  a foreground that was not the game's — before OR after a line — stops the run
///           (F7). Nothing is re-sent, because the input box may hold half a line and the
///           next line would join it into the 247446929 syntax error;
///   rule 4  a line that went in with the foreground held, with no fatal line after it and
///           no marker within its wait, is sent once more — and only then;
///   rule 5  nothing is ever sent to clear the box: Backspaces and End/Shift+Home both made
///           the console drop later lines. The residue is handled by DumpLadder's guard.
/// </summary>
public sealed class ConsoleDriver
{
    private readonly IConsoleKeys _keys;
    private readonly IDumpLogSource _log;
    private readonly DumpPacing _pacing;
    private readonly PaladinLog? _diag;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _now;

    /// <summary>One line, then its Return: two tries in all (§3.6 rule 4).</summary>
    public const int TriesPerLine = 2;
    /// <summary>Two tries on each chord before the next chord (launch-and-dump.ps1:210-217).</summary>
    public const int TriesPerChord = 2;

    public ConsoleDriver(
        IConsoleKeys keys, IDumpLogSource log, DumpPacing pacing, PaladinLog? diag = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null)
    {
        _keys = keys;
        _log = log;
        _pacing = pacing;
        _diag = diag;
        _delay = delay ?? ((span, ct) => span <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(span, ct));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The chord that opened the console, once <see cref="OpenAsync"/> has succeeded.</summary>
    public ConsoleChord? Chord { get; private set; }

    /// <summary>World_GetNumEntities as the ENV line reported it.</summary>
    public int? EnvCount { get; private set; }

    /// <summary>World_GetGameTime at the ENV line: about 6 s of game time.</summary>
    public double? GameTime { get; private set; }

    /// <summary>Every guarded line this driver delivered, in order — the contract DumpLadder.Script states.</summary>
    public List<string> Sent { get; } = new();

    // ---- the mission -------------------------------------------------------------------

    /// <summary>
    /// Step 3 of §3.3: "GAME -- Starting mission:" within the mission timeout, then the
    /// HUD's 3 s. F1 when it never comes — on the night's evidence that means a custom
    /// tournament map the game does not have.
    /// </summary>
    public async Task<DumpFailure?> WaitForMissionAsync(CancellationToken ct)
    {
        _diag?.Info($"Waiting up to {_pacing.MissionTimeoutSeconds} s for \"{DumpLogText.MissionStartText}\"");
        // From line 0: the mission line is printed once, about 20 s in, and the log may
        // already hold it by the time the watcher opened the file.
        var index = await _log.WaitForLineAsync(
            0, line => DumpLogText.IsMissionStart(line) || DumpLogText.IsFatal(line),
            TimeSpan.FromSeconds(_pacing.MissionTimeoutSeconds), ct);

        if (index < 0)
            return DumpFailures.ReplayNeverStarted($"no \"{DumpLogText.MissionStartText}\" in {_log.Lines.Count} lines within {_pacing.MissionTimeoutSeconds} s");

        var line = _log.Lines[index];
        if (DumpLogText.IsFatal(line)) return Fatal(line);

        _diag?.Info($"Mission started at log line {index}; settling {_pacing.HudSettleMs} ms for the HUD");
        await _delay(TimeSpan.FromMilliseconds(_pacing.HudSettleMs), ct);
        return null;
    }

    // ---- the console -------------------------------------------------------------------

    /// <summary>
    /// Step 4: open the console and prove it with the guarded HELLO — two tries on
    /// Alt+Shift, then two on Ctrl+Shift, each failed try toggling the console back so the
    /// next chord opens rather than closes it (launch-and-dump.ps1:208-219). The ENV line
    /// follows HELLO out of the same paste and gives the entity count; a non-numeric count
    /// is F3, the lapsed sign-in.
    /// </summary>
    public async Task<DumpFailure?> OpenAsync(DumpRecord record, CancellationToken ct)
    {
        foreach (var chord in ConsoleChords.InOrder)
        {
            for (var attempt = 1; attempt <= TriesPerChord; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var from = _log.Refresh();

                var toggle = _keys.Chord(chord);
                if (!toggle.Sent)
                    return DumpFailures.FocusLost($"the {ConsoleChords.Name(chord)} chord was not delivered: {toggle.Delivery} ({toggle.Detail})");
                await _delay(TimeSpan.FromMilliseconds(_pacing.ChordSettleMs), ct);

                var hello = await SendAsync(
                    DumpLadder.Guard(DumpLadder.Hello), DumpLadder.HelloMarker,
                    TimeSpan.FromSeconds(_pacing.HelloWaitSeconds), from, tries: 1, ct);

                switch (hello.Outcome)
                {
                    case LineOutcome.Landed:
                        Chord = chord;
                        record.Chord = ConsoleChords.Name(chord);
                        record.InputMethod = _keys.InputMethod;
                        record.Timings.Hello = _now();
                        _diag?.Info($"The console opened with {ConsoleChords.Name(chord)} on try {attempt}");
                        return await ReadEnvAsync(record, hello.Index, ct);

                    case LineOutcome.Fatal:
                        return Fatal(hello.Line);

                    case LineOutcome.FocusLost:
                        return DumpFailures.FocusLost(hello.Detail);
                }

                _diag?.Warn($"No {DumpLadder.HelloMarker} after the {ConsoleChords.Name(chord)} chord (try {attempt} of {TriesPerChord})");
                if (attempt < TriesPerChord)
                {
                    // Toggle the console back, so the next chord opens it rather than closing it.
                    var back = _keys.Chord(chord);
                    if (!back.Sent)
                        return DumpFailures.FocusLost($"the {ConsoleChords.Name(chord)} toggle-back was not delivered: {back.Delivery} ({back.Detail})");
                    await _delay(TimeSpan.FromMilliseconds(DumpPacing.ToggleBackMs), ct);
                }
            }
        }

        return DumpFailures.ConsoleNeverOpened($"no {DumpLadder.HelloMarker} after {TriesPerChord} tries on each of {ConsoleChords.InOrder.Count} chords");
    }

    /// <summary>
    /// The ENV line printed by the same paste as HELLO: the entity count, the game time and
    /// dev mode. A count that is not a number is F3 — the game's sign-in has lapsed and
    /// World_GetNumEntities is not callable, which is what the kit's 113th launch found.
    /// </summary>
    private async Task<DumpFailure?> ReadEnvAsync(DumpRecord record, int helloIndex, CancellationToken ct)
    {
        var index = await _log.WaitForLineAsync(
            Math.Max(0, helloIndex), line => DumpLogText.HasMarker(line, DumpLadder.EnvMarker) || DumpLogText.IsFatal(line),
            TimeSpan.FromSeconds(DumpPacing.EnvWaitSeconds), ct);

        if (index < 0)
            return DumpFailures.DefinitionDropped($"{DumpLadder.HelloMarker} printed but {DumpLadder.EnvMarker} did not follow it");
        if (DumpLogText.IsFatal(_log.Lines[index])) return Fatal(_log.Lines[index]);

        if (!DumpLogText.TryEnv(_log.Lines[index], out var env))
            return DumpFailures.DefinitionDropped($"{DumpLadder.EnvMarker} did not parse: {_log.Lines[index]}");
        if (env.Count is not int count)
            return DumpFailures.NotSignedIn($"{DumpLadder.EnvMarker} count field was '{env.RawCount}', not a number");

        EnvCount = count;
        GameTime = env.GameTime;
        record.EnvCount = count;
        record.GameTimeAtEnv = env.GameTime;
        _diag?.Info($"World_GetNumEntities = {count}; World_GetGameTime = {env.GameTime?.ToString() ?? "?"}; dev mode = {env.DevMode?.ToString() ?? "?"}");
        return null;
    }

    // ---- the ladder, the chunks and the squads ------------------------------------------

    /// <summary>
    /// Steps 6-8: the eleven ladder lines and their PALADIN2_DEF sentinel, BEGIN + PL(),
    /// one PD2 per chunk of <see cref="DumpPacing.ChunkSize"/>, and — only with --squads —
    /// the squad ladder and its call. quit() is not sent (D1 amended).
    /// </summary>
    /// <param name="onChunk">Called with (chunk number, chunk count) before each PD2 goes in.</param>
    public async Task<DumpFailure?> RunScriptAsync(DumpRecord record, bool squads, Action<int, int>? onChunk, CancellationToken ct)
    {
        if (EnvCount is not int entityCount)
            throw new InvalidOperationException("RunScriptAsync needs the entity count; call OpenAsync first.");

        record.Phase = "ladder";

        // The ladder: only its last line prints anything, and that sentinel proves all eleven landed.
        for (var i = 0; i < DumpLadder.EntityLadder.Count; i++)
        {
            var last = i == DumpLadder.EntityLadder.Count - 1;
            var result = await SendAsync(
                DumpLadder.Guard(DumpLadder.EntityLadder[i]),
                last ? DumpLadder.DefMarker : null,
                TimeSpan.FromSeconds(_pacing.DefWaitSeconds), _log.Refresh(), TriesPerLine, ct);

            if (Failure(result) is { } failure) return failure;
            if (last)
            {
                if (!result.Landed)
                    return DumpFailures.DefinitionDropped($"no {DumpLadder.DefMarker} after {result.Tries} tries");
                if (DumpLogText.TryDef(result.Line, out var hasNil) && hasNil)
                {
                    record.DefOk = false;
                    return DumpFailures.DefinitionDropped($"the sentinel carried a nil: {result.Line}");
                }
                record.DefOk = true;
                record.Timings.Def = _now();
            }
        }

        // BEGIN + PL(): the live entity count, the game time and the player rows. The kit
        // warned and carried on when it did not print, and so does this: the player rows are
        // wanted but the entity rows are the dump.
        var begin = await SendAsync(
            DumpLadder.Guard(DumpLadder.Begin), DumpLadder.BeginMarker,
            TimeSpan.FromSeconds(DumpPacing.BeginWaitSeconds), _log.Refresh(), TriesPerLine, ct);
        if (Failure(begin) is { } beginFailure) return beginFailure;
        if (!begin.Landed) _diag?.Warn($"No {DumpLadder.BeginMarker} after {begin.Tries} tries; continuing with the chunks");

        // The chunks.
        record.Phase = "printing";
        var chunks = DumpLadder.Chunks(entityCount, _pacing.ChunkSize);
        for (var i = 0; i < chunks.Count; i++)
        {
            var (a, b) = chunks[i];
            onChunk?.Invoke(i + 1, chunks.Count);
            var marker = DumpLadder.DoneMarker(a, b);
            var result = await SendAsync(
                DumpLadder.Guard(DumpLadder.Chunk(a, b)), marker,
                TimeSpan.FromSeconds(_pacing.ChunkWaitSeconds), _log.Refresh(), TriesPerLine, ct);

            var printed = DumpLogText.TryDone(result.Line, out var done) ? done.Count : 0;
            record.Chunks.Add(new DumpChunkRecord { A = a, B = b, Done = result.Landed, Count = printed, Tries = result.Tries });
            if (result.Landed) record.Timings.LastDone = _now();

            if (Failure(result) is { } chunkFailure) return chunkFailure;
            if (!result.Landed)
                _diag?.Warn($"Chunk {a}-{b} printed no {marker} after {result.Tries} tries");
        }

        if (!squads) return null;

        // The squad ladder is the owner's own queue only; a missing sentinel is a warning,
        // never a failure, because no banked dump needs a PALADIN3_SQ row.
        record.Phase = "squads";
        for (var i = 0; i < DumpLadder.SquadLadder.Count; i++)
        {
            var last = i == DumpLadder.SquadLadder.Count - 1;
            var result = await SendAsync(
                DumpLadder.Guard(DumpLadder.SquadLadder[i]),
                last ? DumpLadder.SquadDefMarker : null,
                TimeSpan.FromSeconds(_pacing.DefWaitSeconds), _log.Refresh(), TriesPerLine, ct);
            if (Failure(result) is { } failure) return failure;
            if (last && !result.Landed)
            {
                _diag?.Warn($"No {DumpLadder.SquadDefMarker} after {result.Tries} tries; the squad dump is skipped");
                return null;
            }
        }

        var call = await SendAsync(
            DumpLadder.Guard(DumpLadder.SquadCall), DumpLadder.SquadBeginMarker,
            TimeSpan.FromSeconds(_pacing.DefWaitSeconds), _log.Refresh(), TriesPerLine, ct);
        if (Failure(call) is { } callFailure) return callFailure;
        if (!call.Landed)
        {
            _diag?.Warn($"No {DumpLadder.SquadBeginMarker} after {call.Tries} tries");
            return null;
        }

        var doneIndex = await _log.WaitForLineAsync(
            call.Index, line => DumpLogText.HasMarker(line, DumpLadder.SquadDoneMarker) || DumpLogText.IsFatal(line),
            TimeSpan.FromSeconds(DumpPacing.SquadDoneWaitSeconds), ct);
        if (doneIndex >= 0 && DumpLogText.IsFatal(_log.Lines[doneIndex])) return Fatal(_log.Lines[doneIndex]);
        if (doneIndex < 0) _diag?.Warn($"{DumpLadder.SquadBeginMarker} printed but no {DumpLadder.SquadDoneMarker} within {DumpPacing.SquadDoneWaitSeconds} s");
        return null;
    }

    // ---- one line ------------------------------------------------------------------------

    /// <summary>
    /// One guarded line: paste, pause, Return, pause, then wait for its marker at or after
    /// <paramref name="fromIndex"/>. The §3.6 rules are all here.
    /// </summary>
    public async Task<LineResult> SendAsync(
        string text, string? marker, TimeSpan wait, int fromIndex, int tries, CancellationToken ct)
    {
        LineResult? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, tries); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var from = attempt == 1 ? fromIndex : _log.Refresh();

            var paste = _keys.PasteLine(text);
            if (!paste.Sent)
                return new LineResult(LineOutcome.FocusLost, -1, null, Describe("the line", paste), attempt);
            Sent.Add(text);
            await _delay(TimeSpan.FromMilliseconds(_pacing.AfterPasteMs), ct);

            var enter = _keys.Enter();
            if (!enter.Sent)
                return new LineResult(LineOutcome.FocusLost, -1, null, Describe("the Return after the line", enter), attempt);
            await _delay(TimeSpan.FromMilliseconds(_pacing.AfterEnterMs), ct);

            if (marker is null)
            {
                // A line that prints nothing has no marker to wait for, so the only thing
                // worth checking is whether the game is dying of it.
                var fatalAfterSilentLine = FirstFatal();
                return fatalAfterSilentLine is null
                    ? new LineResult(LineOutcome.Landed, -1, null, null, attempt)
                    : new LineResult(LineOutcome.Fatal, -1, fatalAfterSilentLine, "a fatal Scar error followed the line", attempt);
            }

            var index = await _log.WaitForLineAsync(
                from, line => DumpLogText.HasMarker(line, marker) || DumpLogText.IsFatal(line), wait, ct);

            if (index >= 0)
            {
                var line = _log.Lines[index];
                return DumpLogText.IsFatal(line)
                    ? new LineResult(LineOutcome.Fatal, index, line, "a fatal Scar error followed the line", attempt)
                    : new LineResult(LineOutcome.Landed, index, line, null, attempt);
            }

            // Rule 4: a retry is allowed only because the foreground was held before AND
            // after this line and nothing fatal followed it. A fatal that arrived late —
            // or one printed by a line that had no marker to wait for — is checked here,
            // over the whole session log, not assumed away.
            var fatal = FirstFatal();
            if (fatal is not null)
                return new LineResult(LineOutcome.Fatal, -1, fatal, "a fatal Scar error followed the line", attempt);

            last = new LineResult(LineOutcome.MarkerMissing, -1, null, $"no {marker} within {wait.TotalSeconds:0} s", attempt);
            if (attempt < tries) _diag?.Warn($"No {marker} within {wait.TotalSeconds:0} s (try {attempt} of {tries}); sending the line again");
        }
        return last ?? new LineResult(LineOutcome.MarkerMissing, -1, null, "not sent", 0);
    }

    /// <summary>The first fatal Scar error anywhere in this session's log, or null. One of them anywhere means the game is closing.</summary>
    private string? FirstFatal()
    {
        _log.Refresh();
        foreach (var line in _log.Lines)
            if (DumpLogText.IsFatal(line)) return line;
        return null;
    }

    /// <summary>A lost line is F7 and a fatal one is F6 — or F3, when the fatal names the nil global a lapsed sign-in leaves behind.</summary>
    private static DumpFailure? Failure(LineResult result) => result.Outcome switch
    {
        LineOutcome.FocusLost => DumpFailures.FocusLost(result.Detail),
        LineOutcome.Fatal => Fatal(result.Line),
        _ => null,
    };

    private static DumpFailure Fatal(string? line) =>
        DumpLogText.IsScriptsUnavailable(line)
            ? DumpFailures.NotSignedIn(line)
            : DumpFailures.FatalScarError(line);

    private static string Describe(string what, KeySendResult result) => result.Delivery switch
    {
        KeyDelivery.NotSent => $"{what} was not sent: the game was not in front ({result.Detail})",
        KeyDelivery.LostAfter => $"{what} went in and the game had lost the foreground by the end ({result.Detail})",
        _ => $"{what} could not be delivered: {result.Detail}",
    };
}
