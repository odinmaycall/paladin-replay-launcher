using System.Runtime.Versioning;
using Paladin.Core.Config;
using Paladin.Core.Dump;
using Paladin.Core.Logging;
using Paladin.Core.Replay;
using Paladin.Core.Shield;
using Paladin.Launcher.Platform;

namespace Paladin.Launcher;

/// <summary>
/// "Dump this game" (§717 §3.3): the pre-flight, the launch through the ordinary
/// <see cref="SessionRunner"/> — the same download, the same Shield snapshot, the same
/// Steam launch with -dev -replay, the same restore — and, while the game is up, the
/// pure <see cref="DumpSession"/> driving the console through this machine's keyboard
/// and reading the game's own log.
///
/// Nothing about the dump is baked into the session runner: this class supplies a hook
/// and gets back an <see cref="ExitPolicy"/>. That is what lets "dump while I watch"
/// (§8) reuse it later without a second copy of anything.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DumpRunner
{
    private readonly LauncherConfig _config;
    private readonly PaladinLog _log;
    private readonly IShieldUi _ui;
    private readonly SessionStore _store;
    private readonly ReplayProviderRegistry _providers;
    private readonly string _version;

    public DumpRunner(
        LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store, ReplayProviderRegistry providers, string version)
    {
        _config = config;
        _log = log;
        _ui = ui;
        _store = store;
        _providers = providers;
        _version = version;
    }

    public bool DryRun { get; init; }

    /// <summary>The run's own result, for the caller that has to decide what to do next (then=watch).</summary>
    public DumpSessionResult? Result { get; private set; }

    /// <summary>
    /// True once the Shield has been handed the run, which is the moment a Ctrl+C stops
    /// meaning "nothing happened": from here on there is a snapshot, and the settings
    /// will be checked and restored. Before it, saying so would be a promise about
    /// nothing (Program.cs's handler reads this).
    /// </summary>
    public bool SessionStarted { get; private set; }

    /// <summary>True once the while-running hook actually began, so a hook that threw is never reported as "the game never started".</summary>
    private bool _dumpBegan;

    public async Task<int> RunAsync(DumpRequest request, CancellationToken ct)
    {
        _ui.Header("dump this game", DumpConsoleText.GameLine(request.GameId, null));

        // ---- 0. Pre-flight -----------------------------------------------------------
        var env = Aoe4Locator.Detect(_config, _log);
        if (!env.CanProtect)
        {
            foreach (var problem in env.Problems) _ui.Fail(problem);
            _ui.Fail("Refusing to dump: Paladin Shield cannot protect settings it cannot find.");
            return ExitCodes.EnvironmentNotFound;
        }

        // F8: a dump has to start the game itself, because it needs the session log this
        // launch writes and the -dev console a normal launch does not have.
        var running = new GameProcessMonitor(_config.GameProcessNames, env.Aoe4GameExePath, _log).Snapshot();
        if (!DryRun && running.Count > 0)
        {
            var failure = DumpFailures.GameAlreadyRunning($"{running.Count} game process(es) are running");
            _ui.Fail(failure.Text);
            return failure.ExitCode;
        }

        var uploader = request.Upload
            ? new DumpUploader(_config.DumpUploadBaseUrl, _version, _log)
            : null;

        if (uploader is not null)
        {
            var preflight = await uploader.CheckAsync(request.GameId, ct);
            var decision = DumpPreflightDecision.For(preflight, request.Force);
            if (!decision.Proceed)
            {
                _ui.Fail(decision.Message);
                return decision.Failure?.ExitCode ?? ExitCodes.Ok;
            }
            if (decision.Warn) _ui.Warn(decision.Message);
            else _ui.Ok(decision.Message);
        }
        else
        {
            _ui.Ok("--no-upload: the rows stay on this PC and Paladin is not asked anything.");
        }

        // The address is the configured one, not a constant: a run pointed at
        // localhost:8787 must not tell the user their rows are going to production.
        var host = uploader?.Host ?? DumpConsoleText.DefaultHost;
        foreach (var line in DumpConsoleText.WhatWillHappen(request.Upload, host)) _ui.Note(line);

        // D3: the consent moment. --yes skips it; so does a console with nothing to press
        // Enter on, because a queue run must not hang.
        if (DumpCountdown.ShouldWait(request.AssumeYes, Console.IsInputRedirected) && !await CountdownAsync(ct))
            return ExitCodes.Cancelled;

        // ---- the watcher must remember LogFiles BEFORE the launch --------------------
        var watcher = new GameLogWatcher(env.Aoe4DocumentsPath!, DateTime.UtcNow, _log);

        KeyboardInjector? keys = null;
        var runner = new SessionRunner(_config, _log, _ui, _store, _providers)
        {
            DryRun = DryRun,
            PrintHeader = false,
            RefuseOnBuildMismatch = true,
            ReplayNameCheck = name => DumpChecks.NameMatchesGame(name, request.GameId)
                ? null
                : DumpChecks.WrongReplayMessage(name, request.GameId),
            // §2.5's Ctrl+C row: this game was started only to be read, so it is ended
            // before the Shield compares and restores, exactly as a finished dump's is.
            CancelPolicy = ExitPolicy.EndProcess(TimeSpan.FromSeconds(_config.DumpCloseGraceSeconds)),
            WhileRunning = async (context, token) =>
            {
                _dumpBegan = true;
                foreach (var line in DumpConsoleText.HandsOff()) _ui.Note(line);
                keys = new KeyboardInjector(context.Pid, context.MainWindow, _log);
                var session = new DumpSession(
                    new DumpSessionOptions(
                        request.GameId, context.SessionId, _version, context.ProcessSeenUtc,
                        DumpPacing.From(_config), request.Squads, request.Upload, context.ReplayBuild),
                    keys, watcher, new ShieldDumpReporter(_ui),
                    new SessionDumpStore(context.SessionDirectory, _log),
                    uploader, _log);

                Result = await session.RunAsync(token);
                return Result.Exit;
            },
        };

        SessionStarted = true;
        var exit = await runner.RunAsync(request.Replay, ct);

        // The user's own clipboard goes back whatever happened, and the console says so.
        if (keys?.RestoreClipboard() is { } clipboardNote) _ui.Note(clipboardNote);

        if (Result?.Failure is { } failed) return failed.ExitCode;

        if (!DryRun && Result is null && exit == ExitCodes.Ok)
        {
            // The dump began and never came back with a result: the hook threw something
            // the session runner caught and reported. Saying "the game never started"
            // after two minutes of watching it run would be the second false line in a row.
            if (_dumpBegan)
            {
                _ui.Fail("The dump stopped on an unexpected error before it could finish. Nothing was sent.");
                _ui.Note("What went wrong was printed above, before the settings check; the log has the rest.");
                return ExitCodes.UnexpectedError;
            }

            // The session ended without the dump ever running: the game process never
            // appeared, or the run was refused before it. A watch would shrug; a dump
            // has to say so.
            _ui.Fail("Age of Empires IV never started, so there was nothing to dump. Nothing was sent.");
            return ExitCodes.LaunchFailed;
        }

        return exit;
    }

    /// <summary>
    /// --dump-upload &lt;session folder or id&gt;: send the envelope an earlier run kept, with
    /// no game and no Shield. This is F11's way out — the rows were printed, the network
    /// was not there, and nothing needs to be printed twice.
    /// </summary>
    public async Task<int> ReSendAsync(string pathOrSessionId, CancellationToken ct)
    {
        var folder = ResolveDumpFolder(pathOrSessionId);
        if (folder is null)
        {
            _ui.Fail($"No kept dump was found for '{pathOrSessionId}'.");
            _ui.Note($"Look under {PathDisplay.ForConsole(_store.SessionsRoot)} for a session folder with a '{SessionDumpStore.FolderName}' in it.");
            return ExitCodes.BadArguments;
        }

        var envelopePath = Path.Combine(folder, SessionDumpStore.EnvelopeFileName);
        DumpEnvelope envelope;
        try { envelope = DumpEnvelope.FromJson(File.ReadAllText(envelopePath)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
        {
            _ui.Fail($"The kept dump could not be read: {ex.Message}");
            return ExitCodes.BadArguments;
        }

        var uploader = new DumpUploader(_config.DumpUploadBaseUrl, _version, _log);

        _ui.Header("dump this game", DumpConsoleText.GameLine(envelope.GameId, null));
        _ui.Pending(DumpConsoleText.Sending(envelope.SizeBytes, uploader.Host));

        var result = await uploader.UploadAsync(envelope, ct);

        var recordPath = Path.Combine(Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar))!, DumpRecord.FileName);
        var record = DumpRecord.TryLoad(recordPath);
        if (record is not null)
        {
            record.Upload.Code = result.HttpCode;
            record.Upload.Body = result.Body;
            record.Upload.At = DateTime.UtcNow;
            record.Upload.Status = result.Accepted ? DumpUploadStatus.Done
                : result.RetryLater ? DumpUploadStatus.Pending
                : DumpUploadStatus.Rejected;
            record.Save(recordPath);
        }

        if (result.Accepted)
        {
            _ui.Ok(DumpConsoleText.Accepted(result.Response));
            _ui.Note(DumpConsoleText.ReloadThePage);
            return ExitCodes.Ok;
        }
        if (result.Outcome == DumpUploadOutcome.Already)
        {
            _ui.Ok(DumpConsoleText.SomeoneElseWasFirst);
            return ExitCodes.Ok;
        }
        if (result.RetryLater)
        {
            _ui.Warn(DumpConsoleText.SendLaterWarn(envelope.Session, uploader.Host));
            return ExitCodes.Ok;
        }

        var failure = DumpFailures.UploadRejected(result.PlainWords, result.Error);
        _ui.Fail(failure.Text);
        return failure.ExitCode;
    }

    /// <summary>A session folder, its dump\ folder, or a session id: all three name the same kept rows.</summary>
    private string? ResolveDumpFolder(string pathOrSessionId)
    {
        var candidates = new[]
        {
            Path.Combine(pathOrSessionId, SessionDumpStore.FolderName),
            pathOrSessionId,
            Path.Combine(_store.SessionDirectory(pathOrSessionId), SessionDumpStore.FolderName),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(candidate, SessionDumpStore.EnvelopeFileName))) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// D3's countdown: five seconds, Enter to go now, Ctrl+C to stop. False means the user
    /// stopped it — including through the Delay, which is where the Ctrl+C the line
    /// invites almost always lands. That cancellation is the answer to a question this
    /// method asked, so it is swallowed here rather than thrown out of the launcher as
    /// "Fatal error: A task was canceled."
    /// </summary>
    private async Task<bool> CountdownAsync(CancellationToken ct)
    {
        _ui.Note(DumpConsoleText.Countdown(DumpCountdown.Seconds));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(DumpCountdown.Seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter) return true;
            }
            catch (InvalidOperationException) { return true; }   // no console input: go
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return !ct.IsCancellationRequested;
    }
}

/// <summary>The dump's console lines, spoken through the launcher's own UI (§717 §2.2).</summary>
public sealed class ShieldDumpReporter : IDumpReporter
{
    private readonly IShieldUi _ui;

    public ShieldDumpReporter(IShieldUi ui) => _ui = ui;

    public void Ok(string message) => _ui.Ok(message);
    public void Pending(string message) => _ui.Pending(message);
    public void Warn(string message) => _ui.Warn(message);
    public void Fail(string message) => _ui.Fail(message);
    public void Note(string message) => _ui.Note(message);
}

/// <summary>
/// Where a run's files go: dump.json in the session folder, and the evidence and the
/// envelope in its dump\ subfolder — the path §717's pass B test T4 names, so that
/// `node scripts/world-dump/run.mjs &lt;id&gt; &lt;session&gt;\dump\evidence.txt` reads it with no
/// new tooling.
/// </summary>
public sealed class SessionDumpStore : IDumpStore
{
    public const string FolderName = "dump";
    public const string EvidenceFileName = "evidence.txt";
    public const string EnvelopeFileName = "envelope.json";

    private readonly string _sessionDirectory;
    private readonly string _dumpDirectory;
    private readonly PaladinLog _log;

    public SessionDumpStore(string sessionDirectory, PaladinLog log)
    {
        _sessionDirectory = sessionDirectory;
        _dumpDirectory = Path.Combine(sessionDirectory, FolderName);
        _log = log;
    }

    public string DisplayFolder => PathDisplay.ForConsole(_dumpDirectory);

    public void SaveRecord(DumpRecord record) => record.Save(Path.Combine(_sessionDirectory, DumpRecord.FileName));

    public string SaveEvidence(string text)
    {
        Directory.CreateDirectory(_dumpDirectory);
        var path = Path.Combine(_dumpDirectory, EvidenceFileName);
        File.WriteAllText(path, text);
        _log.Info($"Kept {text.Length:N0} characters of printed rows at {path}");
        return path;
    }

    public string SaveEnvelope(DumpEnvelope envelope)
    {
        Directory.CreateDirectory(_dumpDirectory);
        var path = Path.Combine(_dumpDirectory, EnvelopeFileName);
        File.WriteAllText(path, envelope.ToJson());
        return path;
    }
}
