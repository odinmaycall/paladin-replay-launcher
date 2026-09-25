using System.Diagnostics;
using System.Runtime.Versioning;
using Paladin.Core.Config;
using Paladin.Core.Deep;
using Paladin.Core.Dump;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;
using Paladin.Core.Replay;
using Paladin.Core.Shield;
using Paladin.Launcher.Platform;

namespace Paladin.Launcher;

/// <summary>
/// §867 — "Deep Capture": the launcher's one capture action.
///
/// Structurally this is <see cref="DumpRunner"/> — the same pre-flight, the same
/// <see cref="SessionRunner"/> doing the same download, Shield snapshot, Steam launch and restore, and
/// the same WhileRunning hook handing the running game to a pure session. Nothing about the launch path
/// is re-derived, because none of it needed to change: what changed is what gets typed into the console
/// and what comes back out.
///
/// WHY THIS EXISTS AS ITS OWN RUNNER RATHER THAN A FLAG ON THE DUMP. A world dump reads the MAP in about
/// two minutes and is judged complete by counting rows against the map's object count. A Deep capture
/// plays fifteen game-minutes and is judged by whether its sample clock reached the build-order window.
/// The two share a launch and nothing else: their failure vocabularies, their completeness tests, their
/// timings and their upload routes are all different. A shared runner with a mode flag would be a
/// permanent invitation to apply one's rules to the other.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeepRunner
{
    private readonly LauncherConfig _config;
    private readonly PaladinLog _log;
    private readonly IShieldUi _ui;
    private readonly SessionStore _store;
    private readonly ReplayProviderRegistry _providers;
    private readonly string _version;

    public DeepRunner(
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

    public DeepSessionResult? Result { get; private set; }

    /// <summary>True once the Shield holds the run, so a Ctrl+C stops meaning "nothing happened".</summary>
    public bool SessionStarted { get; private set; }

    private bool _captureBegan;

    public async Task<int> RunAsync(DumpRequest request, CancellationToken ct)
    {
        _ui.Header(DeepConsoleText.Title, DeepConsoleText.GameLine(request.GameId, null));

        var env = Aoe4Locator.Detect(_config, _log);
        if (!env.CanProtect)
        {
            foreach (var problem in env.Problems) _ui.Fail(problem);
            _ui.Fail("Refusing to capture: Paladin Shield cannot protect settings it cannot find.");
            return ExitCodes.EnvironmentNotFound;
        }

        // A capture has to start the game itself: it needs this launch's own session log and the -dev
        // console a normal launch does not have.
        var monitor = new GameProcessMonitor(_config.GameProcessNames, env.Aoe4GameExePath, _log);
        var running = monitor.Snapshot();
        if (!DryRun && running.Count > 0)
        {
            var failure = DumpFailures.GameAlreadyRunning($"{running.Count} game process(es) are running");
            _ui.Fail(failure.Text);
            return failure.ExitCode;
        }

        var uploader = request.Upload ? new DeepUploader(_config.DumpUploadBaseUrl, _version, _log) : null;

        // §876 — how far this capture has to run. The 15:00 window unless Paladin says this match ended
        // sooner, which it does for the 22% of games that do. Asking is the only way to know: the
        // launcher has no match record, and a compiled-in 900 is what told readers of short games that
        // a complete capture had failed.
        var windowSeconds = DeepLadder.WindowSeconds;

        if (uploader is not null)
        {
            // Asked BEFORE the launch, because a capture costs fifteen minutes of playback and a game
            // whose slot is already held costs fifteen minutes for nothing.
            var preflight = await uploader.CheckAsync(request.GameId, ct);
            if (preflight.AlreadyHeld && !request.Force)
            {
                _ui.Ok($"Paladin already has a Deep Capture for game {request.GameId}. Nothing to do.");
                _ui.Note("Use --force to capture it again anyway.");
                return ExitCodes.Ok;
            }
            if (preflight.Reachable && !preflight.Match)
            {
                _ui.Fail($"Paladin has no record of game {request.GameId}, so a capture could not be stored against it.");
                return ExitCodes.BadArguments;
            }
            if (preflight.Reachable && !preflight.Gzip)
            {
                // The production cadence does not fit down an older Worker's pipe. Saying so now is
                // better than fifteen minutes followed by a 413.
                _ui.Warn($"{uploader.Host} is running an older Paladin that cannot accept a compressed capture.");
                _ui.Warn("The capture will run, but sending it may be refused for size.");
            }
            if (!preflight.Reachable)
                _ui.Warn($"Could not reach {uploader.Host} to check this game first ({preflight.Error}). Capturing anyway; the result is kept on disk either way.");

            // §876 — and the clock, from the same answer. An unreachable or older Paladin leaves the
            // window standing, so this can only ever shorten the wait for a game that really was short.
            windowSeconds = preflight.WindowOr(DeepLadder.WindowSeconds);
            if (windowSeconds < DeepLadder.WindowSeconds)
                _ui.Note(DeepConsoleText.ShortGame(windowSeconds));
        }
        else
        {
            _ui.Ok("--no-upload: the capture stays on this PC and Paladin is not asked anything.");
        }

        var host = uploader?.Host ?? DumpConsoleText.DefaultHost;
        foreach (var line in DeepConsoleText.WhatWillHappen(request.Upload, host)) _ui.Note(line);

        if (DumpCountdown.ShouldWait(request.AssumeYes, Console.IsInputRedirected) && !await CountdownAsync(ct))
            return ExitCodes.Cancelled;

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
            CancelPolicy = ExitPolicy.EndProcess(TimeSpan.FromSeconds(_config.DumpCloseGraceSeconds)),
            WhileRunning = async (context, token) =>
            {
                _captureBegan = true;
                foreach (var line in DeepConsoleText.HandsOff()) _ui.Note(line);
                keys = new KeyboardInjector(context.Pid, context.MainWindow, _log);
                var session = new DeepSession(
                    new DeepSessionOptions(
                        request.GameId, context.SessionId, _version, context.ProcessSeenUtc,
                        DumpPacing.From(_config), request.Upload)
                    {
                        // §876 — the window, or this match's own end when Paladin said it ended sooner.
                        WindowSeconds = windowSeconds,
                    },
                    keys, watcher, new ShieldDumpReporter(_ui),
                    new SessionDeepStore(context.SessionDirectory, _log),
                    uploader, _log,
                    // §870 — so a replay that closes early is noticed in seconds rather than waited out.
                    gameIsRunning: () => monitor.Snapshot().Count > 0);

                Result = await session.RunAsync(token);
                return Result.Exit;
            },
        };

        SessionStarted = true;
        var exit = await runner.RunAsync(request.Replay, ct);

        if (keys?.RestoreClipboard() is { } clipboardNote) _ui.Note(clipboardNote);

        // §869 — ONE SHELL-OPEN DOES BOTH JOBS. On a capture that landed, the reader is taken back to
        // the game's build-order page — which is what they wanted when they clicked Deep Capture — and
        // the same URL carries this launcher's version and capabilities, which is the whole handshake
        // the site needs to stop offering Deep Capture on a launcher that cannot do it.
        //
        // ONLY ON SUCCESS. A failed capture has already said why in this window; yanking the reader's
        // browser to a page with nothing new on it would be the second unhelpful thing in a row.
        /**
         * §879 — AND THE REPLAY IS RETAINED, because this is the last moment it is certainly here.
         *
         * A sampler artifact alone is not the trusted result: the build order's clocks, its Builders
         * column and its landmark placements all come from the REPLAY's order stream. Paladin can only
         * parse a replay it holds, and Microsoft stops serving them about three months after the game,
         * so a capture made today is permanently half-evidenced unless the replay goes up now.
         *
         * AFTER the capture and only on success. A game with no sampler is not made Deep by a replay,
         * and uploading one for a failed capture would bank megabytes for nothing.
         *
         * NEVER FATAL. The capture has already landed and been accepted; if retention fails the game
         * simply reports `partial` and can be completed later without replaying anything. So this
         * neither changes the exit code nor stops the reader being taken to their build order.
         */
        if (Result is { Ok: true } && !DryRun && request.Upload && uploader is not null && runner.PreparedReplayPath is not null)
        {
            var replays = new ReplayUploader(_config.DumpUploadBaseUrl, _version, _log);
            _ui.Note(DeepConsoleText.RetainingReplay());
            var kept = await replays.UploadAsync(request.GameId, runner.PreparedReplayPath!, ct);
            if (kept.Ok) _ui.Ok(DeepConsoleText.ReplayRetained(kept.WireBytes, kept.RawBytes, kept.Parsed));
            else _ui.Warn(DeepConsoleText.ReplayNotRetained(kept.Error));
        }

        if (Result is { Ok: true } && !DryRun) OpenResultPage(request.GameId);

        if (Result?.Failure is { } failed) return failed.ExitCode;

        if (!DryRun && Result is null && exit == ExitCodes.Ok)
        {
            if (_captureBegan)
            {
                _ui.Fail("Deep Capture stopped on an unexpected error before it could finish. Nothing was sent.");
                return ExitCodes.UnexpectedError;
            }
            _ui.Fail("Age of Empires IV never started, so there was nothing to capture. Nothing was sent.");
            return ExitCodes.LaunchFailed;
        }

        return exit;
    }

    /// <summary>
    /// §869 — the game's build-order page, with this launcher's announcement riding along.
    ///
    /// Best effort by design: a machine with no default browser, or a shell that refuses, must not turn
    /// a capture that worked into a run that reports failure.
    /// </summary>
    private void OpenResultPage(long gameId)
    {
        var origin = LauncherCallback.OriginOf(_config.DumpUploadBaseUrl);
        var announce = LauncherCallback.UrlFor(_config.DumpUploadBaseUrl, _version);
        var query = announce is null ? "" : announce[(announce.IndexOf('?') + 1)..];
        var url = $"{origin}build-order?game={gameId}" + (query.Length == 0 ? "" : $"&{query}");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _ui.Note($"Opening the build order: {origin}build-order?game={gameId}");
            _log.Info($"Opened {url}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            _log.Warn($"Could not open the build-order page: {ex.Message}");
            _ui.Note($"See the build order at {origin}build-order?game={gameId}");
        }
    }

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
            catch (InvalidOperationException) { return true; }
            try { await Task.Delay(100, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return !ct.IsCancellationRequested;
    }
}

/// <summary>
/// §867 — where a Deep run's files go: deep.json beside the session, and the rows and the envelope in
/// its deep\ subfolder. A separate folder from the dump's, so a session that did both keeps them apart
/// and `scripts/deep-capture/run.mjs &lt;id&gt; &lt;session&gt;\deep\evidence.txt` reads it with no new tooling.
/// </summary>
public sealed class SessionDeepStore : IDeepStore
{
    public const string FolderName = "deep";
    public const string EvidenceFileName = "evidence.txt";
    public const string EnvelopeFileName = "envelope.json";

    private readonly string _sessionDirectory;
    private readonly string _deepDirectory;
    private readonly PaladinLog _log;

    public SessionDeepStore(string sessionDirectory, PaladinLog log)
    {
        _sessionDirectory = sessionDirectory;
        _deepDirectory = Path.Combine(sessionDirectory, FolderName);
        _log = log;
    }

    public string DisplayFolder => PathDisplay.ForConsole(_deepDirectory);

    public void SaveRecord(DumpRecord record) => record.Save(Path.Combine(_sessionDirectory, "deep.json"));

    public string SaveEvidence(string text)
    {
        Directory.CreateDirectory(_deepDirectory);
        var path = Path.Combine(_deepDirectory, EvidenceFileName);
        File.WriteAllText(path, text);
        _log.Info($"Kept {text.Length:N0} characters of capture rows at {path}");
        return path;
    }

    public string SaveEnvelope(DeepEnvelope envelope)
    {
        Directory.CreateDirectory(_deepDirectory);
        var path = Path.Combine(_deepDirectory, EnvelopeFileName);
        File.WriteAllText(path, envelope.ToJson());
        return path;
    }
}
