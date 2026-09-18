using Paladin.Core.Logging;

namespace Paladin.Core.Dump;

/// <param name="ProcessSeenUtc">When the game's process appeared: every offset in the envelope's timings is measured from it (§717 §3.3).</param>
public sealed record DumpSessionOptions(
    long GameId,
    string SessionId,
    string LauncherVersion,
    DateTime ProcessSeenUtc,
    DumpPacing Pacing,
    bool Squads = false,
    bool Upload = true,
    int? ReplayBuild = null);

/// <param name="Failure">Null when the run did what it set out to do.</param>
/// <param name="Exit">What the session runner should do with the game now.</param>
public sealed record DumpSessionResult(
    DumpFailure? Failure,
    ExitPolicy Exit,
    DumpRecord Record,
    DumpEvidence? Evidence = null,
    DumpUploadResult? Upload = null)
{
    public bool Ok => Failure is null;
}

/// <summary>
/// The run of §717 §3.3 from the moment the game's process exists to the moment the
/// launcher may end it — the console, the rows, the evidence, the record and the upload —
/// with every platform thing behind an interface: the keyboard (<see cref="IConsoleKeys"/>),
/// the game's log (<see cref="IDumpLogSource"/>), the console (<see cref="IDumpReporter"/>),
/// the disk (<see cref="IDumpStore"/>) and Paladin (<see cref="IDumpUploadService"/>).
///
/// It launches nothing, ends nothing and restores nothing: it returns an
/// <see cref="ExitPolicy"/> and the launcher's DumpRunner does those, exactly as §8 asks,
/// so "dump while I watch" and a later --attach reuse this untouched.
///
/// The ordering that matters and is tested: the evidence and the record are on disk
/// BEFORE the result asks for the game to be ended, and a run that failed still saves
/// what was printed and sends nothing.
/// </summary>
public sealed class DumpSession
{
    private readonly DumpSessionOptions _options;
    private readonly IConsoleKeys _keys;
    private readonly IDumpLogSource _log;
    private readonly IDumpReporter _ui;
    private readonly IDumpStore _store;
    private readonly IDumpUploadService? _uploader;
    private readonly PaladinLog? _diag;
    private readonly ConsoleDriver _driver;
    private readonly Func<DateTime> _now;

    public DumpSession(
        DumpSessionOptions options, IConsoleKeys keys, IDumpLogSource log, IDumpReporter ui, IDumpStore store,
        IDumpUploadService? uploader = null, PaladinLog? diag = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTime>? now = null)
    {
        _options = options;
        _keys = keys;
        _log = log;
        _ui = ui;
        _store = store;
        _uploader = uploader;
        _diag = diag;
        _now = now ?? (() => DateTime.UtcNow);
        _driver = new ConsoleDriver(keys, log, options.Pacing, diag, delay, _now);
    }

    /// <summary>The driver, for a caller that wants the chord or the lines it sent.</summary>
    public ConsoleDriver Driver => _driver;

    /// <summary>The address the console names when it talks about sending (never a constant: §5.5 points it elsewhere).</summary>
    private string Host => _uploader?.Host ?? DumpConsoleText.DefaultHost;

    public async Task<DumpSessionResult> RunAsync(CancellationToken ct)
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
            // Ctrl+C. The cancellation belongs to the launcher — it ends the game and runs
            // the restore — but the record on disk is this run's only account of itself,
            // and a stale "console" phase would read as a run that vanished mid-sentence.
            record.Phase = "failed";
            record.Failure = "interrupted: the run was stopped before it finished";
            if (record.Upload.Status == DumpUploadStatus.Pending) record.Upload.Status = DumpUploadStatus.Skipped;
            _store.SaveRecord(record);
            _diag?.Warn("Dump interrupted by the user.");
            throw;
        }
    }

    private async Task<DumpSessionResult> RunCoreAsync(DumpRecord record, CancellationToken ct)
    {
        // ---- the game's own log ----------------------------------------------------
        // Not F1: the game may have started perfectly well and written its log somewhere
        // Paladin did not look (a redirected Documents folder, a LogFiles it cannot
        // enumerate). Sending that user off to find a custom map's mod would be a lie.
        if (!await _log.WaitForSessionLogAsync(TimeSpan.FromSeconds(DumpPacing.SessionLogWaitSeconds), ct))
            return Failed(record, DumpFailures.GameLogNotFound(
                _log.LogFilesDisplayPath,
                $"no new session folder with a {GameLogFiles.SessionLogPattern} within {DumpPacing.SessionLogWaitSeconds} s"), null);
        _diag?.Info($"Tailing {_log.SessionLogPath}");

        // ---- the mission -----------------------------------------------------------
        record.Phase = "mission";
        _store.SaveRecord(record);
        if (await _driver.WaitForMissionAsync(ct) is { } missionFailure)
            return Failed(record, missionFailure, null);
        record.Timings.MissionStart = _now();
        _ui.Ok(DumpConsoleText.ReplayStarted);

        // ---- the console -----------------------------------------------------------
        record.Phase = "console";
        _store.SaveRecord(record);
        if (await _driver.OpenAsync(record, ct) is { } consoleFailure)
        {
            TryBuildEvidence(record, out var printedSoFar, out _);
            return Failed(record, consoleFailure, printedSoFar);
        }
        _store.SaveRecord(record);
        _ui.Ok(DumpConsoleText.ConsoleOpen(record.EnvCount ?? 0));
        if (_log.Lines.Any(DumpLogText.IsAccountPrompt))
            _diag?.Info("The -dev account prompt is up; it does not block the dump and is left alone.");

        // ---- the ladder, the rows, the squads ---------------------------------------
        var scriptFailure = await _driver.RunScriptAsync(
            record, _options.Squads, (i, n) => _ui.Pending(DumpConsoleText.PrintingChunk(i, n)), ct);

        // ---- the evidence, whatever happened ----------------------------------------
        record.Phase = "evidence";
        if (!TryBuildEvidence(record, out var evidence, out var refused))
            return Failed(record, refused!, null);

        if (evidence is not null)
        {
            record.EvidencePath = _store.SaveEvidence(evidence.Text);
            record.Bytes = evidence.Bytes;
            _store.SaveRecord(record);
            _diag?.Info($"Evidence: {evidence.EntityRows} rows, {evidence.HeaderLines.Count} header lines, {evidence.Bytes:N0} bytes -> {record.EvidencePath}");
        }

        if (scriptFailure is not null) return Failed(record, scriptFailure, evidence);

        var seconds = (record.Timings.LastDone ?? _now()) - (record.Timings.Hello ?? _options.ProcessSeenUtc);
        _ui.Ok(DumpConsoleText.Printed(evidence?.EntityRows ?? 0, record.EnvCount ?? 0, evidence?.ErrCount ?? 0, seconds.TotalSeconds));

        // ---- completeness (F5) -------------------------------------------------------
        if (evidence is null || !evidence.Complete)
            return Failed(record, DumpFailures.Incomplete(
                evidence?.EntityRows ?? 0, record.EnvCount ?? 0, _store.DisplayFolder,
                $"{record.Chunks.Count(c => c.Done)} of {record.Chunks.Count} chunks printed a DONE line"), evidence);

        // ---- the upload ---------------------------------------------------------------
        // Read the input method again rather than trusting the one recorded at the start:
        // a clipboard that refused mid-run turns every later line into typed Unicode, and
        // the envelope has to say which path the rows actually went in by.
        record.InputMethod = _keys.InputMethod;

        var envelope = DumpEnvelope.From(
            evidence, _options.GameId, _options.LauncherVersion, _options.SessionId,
            record.Timings.LastDone ?? _now(), _options.ReplayBuild,
            record.Chord ?? "", record.InputMethod ?? _keys.InputMethod, Timings(record));
        _store.SaveEnvelope(envelope);

        if (!_options.Upload || _uploader is null)
        {
            record.Upload.Status = DumpUploadStatus.Skipped;
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Ok(DumpConsoleText.KeptLocally(evidence.Bytes, _store.DisplayFolder));
            return new DumpSessionResult(null, Exit(), record, evidence);
        }

        record.Phase = "uploading";
        _store.SaveRecord(record);
        _ui.Pending(DumpConsoleText.Sending(envelope.SizeBytes, Host));

        var upload = await _uploader.UploadAsync(envelope, ct);
        record.Upload.Code = upload.HttpCode;
        record.Upload.Body = upload.Body;
        record.Upload.At = _now();
        record.Timings.Uploaded = _now();

        if (upload.Accepted)
        {
            record.Upload.Status = DumpUploadStatus.Done;
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Ok(DumpConsoleText.Accepted(upload.Response));
            _ui.Note(DumpConsoleText.ReloadThePage);
            return new DumpSessionResult(null, Exit(), record, evidence, upload);
        }

        if (upload.Outcome == DumpUploadOutcome.Already)
        {
            // The slot was taken by someone else's verified dump between the pre-flight and
            // the send. Nothing is wrong with this run; there is simply nothing to store.
            record.Upload.Status = DumpUploadStatus.Rejected;
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Ok(DumpConsoleText.SomeoneElseWasFirst);
            return new DumpSessionResult(null, Exit(), record, evidence, upload);
        }

        if (upload.RetryLater)
        {
            // F11: a warning, not a failure. The rows are on disk and --dump-upload re-sends them.
            record.Upload.Status = DumpUploadStatus.Pending;
            record.Phase = "done";
            _store.SaveRecord(record);
            _ui.Warn(DumpConsoleText.SendLaterWarn(_options.SessionId, Host));
            return new DumpSessionResult(null, Exit(), record, evidence, upload);
        }

        record.Upload.Status = DumpUploadStatus.Rejected;
        return Failed(record, DumpFailures.UploadRejected(upload.PlainWords, upload.Error), evidence) with { Upload = upload };
    }

    /// <summary>The envelope's three offsets, in seconds from the process appearing (§4.2).</summary>
    private DumpTimings Timings(DumpRecord record) => new()
    {
        MissionStartS = Offset(record.Timings.MissionStart),
        HelloS = Offset(record.Timings.Hello),
        DumpS = Offset(record.Timings.LastDone),
    };

    private double Offset(DateTime? at) =>
        at is null ? 0 : Math.Round((at.Value - _options.ProcessSeenUtc).TotalSeconds, 3);

    /// <summary>
    /// The evidence from the tailed session file and the top-level log's head. False only
    /// when the builder refused a line that must never leave the machine, which cannot
    /// happen through its own selectors and is therefore a stop, not a warning.
    /// </summary>
    private bool TryBuildEvidence(DumpRecord record, out DumpEvidence? evidence, out DumpFailure? refused)
    {
        evidence = null;
        refused = null;
        _log.Refresh();
        if (_log.Lines.Count == 0) return true;
        try
        {
            evidence = DumpEvidence.Build(_log.ReadTopLevelHead(), _log.TextSoFar);
            record.Fatal = evidence.Summary.Fatal;
            return true;
        }
        catch (DumpEvidenceException ex)
        {
            _diag?.Error("The evidence builder refused to keep a line", ex);
            refused = DumpFailures.EvidenceRefused(ex.Message);
            return false;
        }
    }

    /// <summary>D1 amended: the launcher ends the game itself once the evidence is safe. Attended mode leaves it to the user.</summary>
    private ExitPolicy Exit() =>
        _options.Pacing.EndProcess
            ? ExitPolicy.EndProcess(TimeSpan.FromSeconds(_options.Pacing.CloseGraceSeconds))
            : ExitPolicy.WaitForUser;

    private DumpSessionResult Failed(DumpRecord record, DumpFailure failure, DumpEvidence? evidence)
    {
        record.Phase = "failed";
        record.Failure = failure.ToString();
        if (record.Upload.Status == DumpUploadStatus.Pending && failure.Code != "F11")
            record.Upload.Status = DumpUploadStatus.Skipped;
        _store.SaveRecord(record);
        _diag?.Warn($"Dump failed: {failure}");
        _ui.Fail(failure.Text);
        return new DumpSessionResult(failure, Exit(), record, evidence);
    }
}
