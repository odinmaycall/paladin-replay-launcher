using Paladin.Core.Dump;
using Paladin.Core.Logging;

namespace Paladin.Core.Deep;

/// <param name="ProcessSeenUtc">When the game's process appeared; every reported offset is measured from it.</param>
public sealed record DeepSessionOptions(
    long GameId,
    string SessionId,
    string LauncherVersion,
    DateTime ProcessSeenUtc,
    DumpPacing Pacing,
    bool Upload = true,
    int RepairAttempts = 2,
    int WindowSeconds = DeepLadder.WindowSeconds);

/// <param name="Failure">Null when the capture did what it set out to do.</param>
public sealed record DeepSessionResult(
    DumpFailure? Failure,
    ExitPolicy Exit,
    DumpRecord Record,
    int Samples = 0,
    int FirstSample = -1,
    int LastSample = -1,
    IReadOnlyList<string>? Rows = null,
    DumpUploadResult? Upload = null)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// §867 — A DEEP CAPTURE, from the moment the game's process exists to the moment it may be ended.
///
/// The shape is <see cref="DumpSession"/>'s, deliberately: the same console driver, the same log source,
/// the same reporter, the same ExitPolicy contract. What is different is what goes down the console and
/// how failure is detected.
///
/// THE GATE IS THE WHOLE DESIGN. Paladin measured twenty real launches of the kit's driver and four
/// failed, every one of them a DROPPED PASTE LINE. The kit defines the sampler and calls it in one
/// paste, so a lost definition is invisible until the replay has run its full fifteen minutes and
/// produced nothing — two of the four failures were exactly that. Here the order is:
///
///   freeze -> definitions -> ASK THE SAMPLER IF IT IS COMPLETE -> repair what is missing -> only then SQ()
///
/// A dropped line costs two seconds instead of a capture. And because the freeze and the thaw both live
/// inside SQ(), which is sent last and only when the bootstrap is sound, there is no path that commits
/// to sampling on a broken install.
///
/// NOTHING IS EVER LEFT FROZEN. Every exit from the bootstrap — refusal, repair exhausted, a fatal Scar
/// error — sends <see cref="DeepLadder.Thaw"/> first. The launcher ends the process afterwards in any
/// case, but a game that is going to be closed should not spend the interval stopped dead.
/// </summary>
public sealed class DeepSession
{
    private readonly DeepSessionOptions _options;
    private readonly IConsoleKeys _keys;
    private readonly IDumpLogSource _log;
    private readonly IDumpReporter _ui;
    private readonly IDeepStore _store;
    private readonly DeepUploader? _uploader;
    private readonly PaladinLog? _diag;
    private readonly ConsoleDriver _driver;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>How long to wait for a sample to appear once the sampler is installed and thawed.</summary>
    public const int FirstSampleWaitSeconds = 30;

    /// <summary>How long the capture may go without the clock advancing before it is called stalled.</summary>
    public const int StallSeconds = 300;

    /// <summary>
    /// §870 — is the game still up? Null means "cannot tell", and the watch then behaves exactly as it
    /// did: it waits out the stall timeout. capture.ps1 has always checked this; the launcher did not,
    /// so a replay that ended early sat for five idle minutes before the loop gave up on it.
    /// </summary>
    private readonly Func<bool>? _gameIsRunning;

    public DeepSession(
        DeepSessionOptions options, IConsoleKeys keys, IDumpLogSource log, IDumpReporter ui, IDeepStore store,
        DeepUploader? uploader = null, PaladinLog? diag = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null,
        Func<bool>? gameIsRunning = null)
    {
        _gameIsRunning = gameIsRunning;
        _options = options;
        _keys = keys;
        _log = log;
        _ui = ui;
        _store = store;
        _uploader = uploader;
        _diag = diag;
        _now = now ?? (() => DateTime.UtcNow);
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _driver = new ConsoleDriver(keys, log, options.Pacing, diag, delay, _now);
    }

    public ConsoleDriver Driver => _driver;

    public async Task<DeepSessionResult> RunAsync(CancellationToken ct)
    {
        var record = new DumpRecord
        {
            GameId = _options.GameId,
            SessionId = _options.SessionId,
            Phase = "launched",
            InputMethod = _keys.InputMethod,
        };
        record.Timings.ProcessSeen = _options.ProcessSeenUtc;
        _store.SaveRecord(record);

        try
        {
            return await RunCoreAsync(record, ct);
        }
        catch (OperationCanceledException)
        {
            record.Phase = "failed";
            record.Failure = "interrupted: the capture was stopped before it finished";
            _store.SaveRecord(record);
            _diag?.Warn("Deep capture interrupted by the user.");
            throw;
        }
    }

    private async Task<DeepSessionResult> RunCoreAsync(DumpRecord record, CancellationToken ct)
    {
        if (!await _log.WaitForSessionLogAsync(TimeSpan.FromSeconds(DumpPacing.SessionLogWaitSeconds), ct))
            return Failed(record, DumpFailures.GameLogNotFound(
                _log.LogFilesDisplayPath,
                $"no new session folder with a {GameLogFiles.SessionLogPattern} within {DumpPacing.SessionLogWaitSeconds} s"));

        record.Phase = "mission";
        _store.SaveRecord(record);
        if (await _driver.WaitForMissionAsync(ct) is { } missionFailure) return Failed(record, missionFailure);
        record.Timings.MissionStart = _now();
        _ui.Ok(DeepConsoleText.ReplayStarted);

        // ---- the console ------------------------------------------------------------------
        record.Phase = "console";
        _store.SaveRecord(record);
        if (await _driver.OpenAsync(record, ct) is { } consoleFailure) return Failed(record, consoleFailure);
        _store.SaveRecord(record);
        _ui.Ok(DeepConsoleText.ConsoleOpen(record.EnvCount ?? 0));

        // ---- the bootstrap ----------------------------------------------------------------
        record.Phase = "bootstrap";
        _store.SaveRecord(record);
        var installed = await InstallAsync(record, ct);
        if (installed is not null)
        {
            await ThawAsync(ct);
            return Failed(record, installed);
        }

        // ---- the capture ------------------------------------------------------------------
        record.Phase = "capturing";
        _store.SaveRecord(record);
        var reached = await WatchAsync(ct);
        var rows = DumpEvidence.DeepLines(_log.TextSoFar);
        var clocks = DeepClocks.From(rows);

        if (rows.Count > 0)
        {
            record.EvidencePath = _store.SaveEvidence(string.Join(Environment.NewLine, rows));
            record.Bytes = rows.Sum(r => r.Length);
            _store.SaveRecord(record);
        }

        if (clocks.Count == 0)
            return Failed(record, DeepFailures.NoSamples("the sampler installed and reported ready, and printed no rows"));

        _ui.Ok(DeepConsoleText.Captured(clocks.Count, clocks.First, clocks.Last));

        if (!reached || clocks.Last < _options.WindowSeconds)
            return Failed(record, DeepFailures.Incomplete(clocks.Last, _options.WindowSeconds, _store.DisplayFolder)) with
            {
                Samples = clocks.Count,
                FirstSample = clocks.First,
                LastSample = clocks.Last,
                Rows = rows,
            };

        // ---- the upload -------------------------------------------------------------------
        var envelope = DeepEnvelope.From(_options.GameId, rows);
        _store.SaveEnvelope(envelope);

        if (!_options.Upload || _uploader is null)
        {
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Ok(DeepConsoleText.KeptLocally(envelope.JsonBytes, _store.DisplayFolder));
            return new DeepSessionResult(null, Exit(), record, clocks.Count, clocks.First, clocks.Last, rows);
        }

        record.Phase = "uploading";
        _store.SaveRecord(record);
        var gzipBytes = envelope.ToGzip().Length;
        _ui.Pending(DeepConsoleText.Sending(envelope.JsonBytes, gzipBytes, _uploader.Host));

        var upload = await _uploader.UploadAsync(envelope, ct);
        record.Upload.Code = upload.HttpCode;
        record.Upload.Body = upload.Body;
        record.Upload.At = _now();

        if (upload.Accepted || upload.Outcome == DumpUploadOutcome.Already)
        {
            record.Upload.Status = upload.Accepted ? DumpUploadStatus.Done : DumpUploadStatus.Rejected;
            record.Phase = "done";
            _store.SaveRecord(record);
            if (upload.Accepted) _ui.Ok(DeepConsoleText.Accepted(clocks.Count));
            else _ui.Ok(DeepConsoleText.SomeoneElseWasFirst);
            _ui.Note(DeepConsoleText.ReloadThePage);
            return new DeepSessionResult(null, Exit(), record, clocks.Count, clocks.First, clocks.Last, rows, upload);
        }

        if (upload.RetryLater)
        {
            record.Upload.Status = DumpUploadStatus.Pending;
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Warn(DeepConsoleText.SendLaterWarn(_store.DisplayFolder));
            return new DeepSessionResult(null, Exit(), record, clocks.Count, clocks.First, clocks.Last, rows, upload);
        }

        record.Upload.Status = DumpUploadStatus.Rejected;
        return Failed(record, DumpFailures.UploadRejected(upload.PlainWords, upload.Error)) with
        {
            Samples = clocks.Count,
            FirstSample = clocks.First,
            LastSample = clocks.Last,
            Rows = rows,
            Upload = upload,
        };
    }

    /// <summary>
    /// §867 — freeze, define, CHECK, repair, go. Returns null when the sampler is installed and running.
    /// </summary>
    private async Task<DumpFailure?> InstallAsync(DumpRecord record, CancellationToken ct)
    {
        // 1. Freeze. Its own marker carries the rate and the clock it stopped at, so "did it work" is
        //    answered by the game rather than assumed.
        var freeze = await _driver.SendAsync(
            DeepLadder.Freeze, DeepLadder.FreezeMarker, TimeSpan.FromSeconds(_options.Pacing.DefWaitSeconds), _log.Refresh(), ConsoleDriver.TriesPerLine, ct);
        if (!freeze.Landed) return DeepFailures.BootstrapLost($"the freeze line did not land: {freeze.Detail ?? freeze.Outcome.ToString()}");
        _diag?.Info($"Frozen: {freeze.Line}");
        _ui.Ok(DeepConsoleText.Frozen);

        // 2. The definitions. Each prints nothing, so there is no marker to wait for; the self-check
        //    below is what proves they landed.
        _ui.Pending(DeepConsoleText.Installing(DeepLadder.Definitions.Count));
        if (await SendDefinitionsAsync(DeepLadder.Definitions, ct) is { } defFailure) return defFailure;

        // 3. ASK, then repair only what was lost, then ask again. The check's answer is carried round
        //    the loop rather than re-queried, so a repair costs exactly one extra self-check.
        for (var repair = 0; repair <= Math.Max(0, _options.RepairAttempts); repair++)
        {
            var missing = await CheckAsync(ct);
            if (missing is null) return DeepFailures.BootstrapLost("the sampler's self-check never answered");

            if (missing.Count == 0)
            {
                _ui.Ok(DeepConsoleText.Installed(repair + 1));
                // 4. Sample, register and thaw — the one line the kit proved, so the game is never
                //    left stopped by a half-finished bootstrap.
                var go = await _driver.SendAsync(
                    DeepLadder.Go, DeepLadder.SquadDoneMarker, TimeSpan.FromSeconds(_options.Pacing.DefWaitSeconds), _log.Refresh(), ConsoleDriver.TriesPerLine, ct);
                if (!go.Landed) return DeepFailures.BootstrapLost($"the sampler was complete and SQ() did not answer: {go.Detail ?? go.Outcome.ToString()}");
                record.Timings.LastDone = _now();
                _store.SaveRecord(record);
                _ui.Ok(DeepConsoleText.Running(DeepLadder.Cadence, DeepLadder.SimRate));
                return null;
            }

            if (repair == _options.RepairAttempts)
                return DeepFailures.BootstrapLost(
                    $"the console kept dropping definition lines: {string.Join(", ", missing)} still missing after {_options.RepairAttempts} repair(s)");

            // Re-send only the lines that define what is missing. A name we cannot place — V5WHO
            // itself, or a helper defined alongside others — means re-sending all of them.
            var toSend = DeepLadder.Definitions.Where(line => missing.Any(name => DefinesName(line, name))).ToList();
            if (toSend.Count == 0) toSend = DeepLadder.Definitions.ToList();

            _ui.Warn(DeepConsoleText.Repairing(missing, repair + 1, _options.RepairAttempts));
            _diag?.Warn($"The console dropped {string.Join(", ", missing)}; re-sending {toSend.Count} definition line(s)");
            if (await SendDefinitionsAsync(toSend, ct) is { } repairFailure) return repairFailure;
        }

        return DeepFailures.BootstrapLost("the sampler could not be installed");
    }

    /// <summary>Paste a set of definition lines. Null when they all went in without the game dying.</summary>
    private async Task<DumpFailure?> SendDefinitionsAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            var sent = await _driver.SendAsync(line, null, TimeSpan.Zero, _log.Refresh(), tries: 1, ct);
            if (sent.Outcome == LineOutcome.Fatal) return DeepFailures.BootstrapLost($"a fatal Scar error followed a definition: {sent.Line}");
            if (sent.Outcome == LineOutcome.FocusLost) return DumpFailures.FocusLost(sent.Detail);
        }
        return null;
    }

    /// <summary>
    /// Ask the sampler whether it is complete. Returns the missing helper names (empty when all landed)
    /// or null when the check itself never answered.
    /// </summary>
    private async Task<IReadOnlyList<string>?> CheckAsync(CancellationToken ct)
    {
        var from = _log.Refresh();
        var sent = await _driver.SendAsync(DeepLadder.SelfCheck, null, TimeSpan.Zero, from, tries: 1, ct);
        if (sent.Outcome is LineOutcome.Fatal or LineOutcome.FocusLost) return null;

        var index = await _log.WaitForLineAsync(
            from,
            line => DumpLogText.HasMarker(line, DeepLadder.AllOkMarker) || DumpLogText.HasMarker(line, DeepLadder.MissingMarker),
            TimeSpan.FromSeconds(_options.Pacing.DefWaitSeconds), ct);
        if (index < 0) return null;

        var answer = _log.Lines[index];
        return DumpLogText.HasMarker(answer, DeepLadder.AllOkMarker) ? Array.Empty<string>() : DeepLadder.MissingFrom(answer);
    }

    /// <summary>Does this definition line define <paramref name="name"/>? Used to re-send only what was lost.</summary>
    private static bool DefinesName(string line, string name) =>
        line.Contains($"function {name}(", StringComparison.Ordinal);

    /// <summary>Watch the clock advance to the window. True when it got there.</summary>
    private async Task<bool> WatchAsync(CancellationToken ct)
    {
        var best = -1;
        var lastProgress = _now();
        var started = _now();
        var deadline = _now().AddSeconds(FirstSampleWaitSeconds);

        while (!ct.IsCancellationRequested)
        {
            await _delay(TimeSpan.FromSeconds(5), ct);
            _log.Refresh();
            var clocks = DeepClocks.From(DumpEvidence.DeepLines(_log.TextSoFar));

            if (clocks.Count > 0 && clocks.Last > best)
            {
                if (best < 0) _ui.Ok(DeepConsoleText.FirstSample(clocks.First));
                best = clocks.Last;
                lastProgress = _now();
                _ui.Pending(DeepConsoleText.Progress(best, _options.WindowSeconds));
            }

            if (best >= _options.WindowSeconds) return true;

            // §870 — the game going away is an ANSWER, not something to wait out. Nothing more can be
            // sampled from a replay that has closed, so the five-minute stall timeout only delays a
            // verdict the loop could give at once.
            if (GameHasGoneAway(started)) {
                _diag?.Warn($"The game is no longer running; stopping the watch at {best}s.");
                return false;
            }

            if (best < 0 && _now() > deadline) return false;
            if (best >= 0 && (_now() - lastProgress).TotalSeconds > StallSeconds) return false;
        }
        return false;
    }

    private bool GameHasGoneAway(DateTime started) =>
        _gameIsRunning is not null && ShouldStopForMissingGame(_gameIsRunning(), (_now() - started).TotalSeconds);

    /// <summary>
    /// §870 — should a watch stop because the game is gone? Pure, so the grace window is testable.
    ///
    /// THE GRACE EXISTS BECAUSE THE SIGNAL CAN LIE EARLY. The session begins as soon as the process is
    /// seen, and a process snapshot taken in the first seconds of a launch can miss a game that is very
    /// much alive. Nothing is lost by waiting: a replay that really has closed is still noticed in
    /// seconds rather than after the five-minute stall timeout.
    /// </summary>
    public const int GameGoneGraceSeconds = 30;

    public static bool ShouldStopForMissingGame(bool gameRunning, double elapsedSeconds) =>
        !gameRunning && elapsedSeconds >= GameGoneGraceSeconds;

    /// <summary>Put the game back to a running rate. Best effort: the caller is on its way out either way.</summary>
    private async Task ThawAsync(CancellationToken ct)
    {
        try
        {
            await _driver.SendAsync(DeepLadder.Thaw, DeepLadder.ThawMarker, TimeSpan.FromSeconds(4), _log.Refresh(), tries: 1, ct);
            _diag?.Info("Sent a thaw on the way out, so the replay is not left stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _diag?.Warn($"The thaw on the way out could not be sent: {ex.Message}");
        }
    }

    private ExitPolicy Exit() =>
        _options.Pacing.EndProcess
            ? ExitPolicy.EndProcess(TimeSpan.FromSeconds(_options.Pacing.CloseGraceSeconds))
            : ExitPolicy.WaitForUser;

    private DeepSessionResult Failed(DumpRecord record, DumpFailure failure)
    {
        record.Phase = "failed";
        record.Failure = failure.ToString();
        _store.SaveRecord(record);
        _diag?.Warn($"Deep capture failed: {failure}");
        _ui.Fail(failure.Text);
        return new DeepSessionResult(failure, Exit(), record);
    }
}

/// <summary>Where a Deep run's files go. Mirrors the dump's store so a session folder reads the same way.</summary>
public interface IDeepStore
{
    string DisplayFolder { get; }
    void SaveRecord(DumpRecord record);
    string SaveEvidence(string text);
    string SaveEnvelope(DeepEnvelope envelope);
}

/// <summary>The sample clocks in a capture's rows: how many, the first and the last.</summary>
public sealed record DeepClocks(int Count, int First, int Last)
{
    public static DeepClocks From(IEnumerable<string> rows)
    {
        var clocks = new List<int>();
        foreach (var row in rows)
        {
            var at = row.IndexOf(DeepLadder.SampleMarker, StringComparison.Ordinal);
            if (at < 0) continue;
            var bar = row.IndexOf('|', at);
            if (bar < 0) continue;
            var end = row.IndexOf('|', bar + 1);
            var text = end < 0 ? row[(bar + 1)..] : row[(bar + 1)..end];
            if (int.TryParse(text, out var t)) clocks.Add(t);
        }
        return clocks.Count == 0 ? new DeepClocks(0, -1, -1) : new DeepClocks(clocks.Count, clocks.Min(), clocks.Max());
    }
}
