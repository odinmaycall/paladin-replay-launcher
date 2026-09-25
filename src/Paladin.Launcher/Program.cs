using System.Reflection;
using System.Runtime.Versioning;
using Paladin.Core.Config;
using Paladin.Core.Dump;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;
using Paladin.Core.Replay;
using Paladin.Core.Shield;
using Paladin.Launcher;
using Paladin.Launcher.Platform;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    private const string AppFolderName = "PaladinReplayLauncher";

    /**
     * The window must not vanish before the outcome has been read.
     *
     * Started from a paladin:// link the process OWNS the console window Windows opened for it, so
     * the window closes the instant Main returns. Every fast outcome therefore looks identical to
     * the reader: a black window that flashes and is gone. The owner reported it twice - first on
     * "Dump this game" against launcher 0.3.0, which answered "Unsupported paladin action 'dump'"
     * and exited, and again on "Dump, then watch", which correctly refused a game that already had
     * its world layer. Neither was a crash and neither could be read.
     *
     * So the outcome is held on screen, but ONLY where a human is there to dismiss it. The dump
     * queue runs this exe unattended for hours (the Shield, as --observe --yes with its output
     * redirected), and a blocking read there would hang the night.
     */
    private static async Task<int> Main(string[] rawArgs)
    {
        var exitCode = await RunAsync(rawArgs);
        HoldWindowIfItIsOurs(rawArgs, exitCode);
        return exitCode;
    }

    /// <summary>Only this process is attached to the console: the window is ours and dies with us.</summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    private static bool OwnsItsConsoleWindow()
    {
        try
        {
            var buffer = new uint[4];
            return GetConsoleProcessList(buffer, (uint)buffer.Length) == 1;
        }
        catch
        {
            // No console at all, or the call is unavailable: either way we are not the one closing a window.
            return false;
        }
    }

    private static void HoldWindowIfItIsOurs(string[] rawArgs, int exitCode)
    {
        // Redirected streams mean a caller is reading us - the queue, a script, a test harness.
        if (Console.IsOutputRedirected || Console.IsInputRedirected) return;
        foreach (var a in rawArgs)
        {
            // The unattended flags, belt and braces behind the redirect test above.
            if (string.Equals(a, "--yes", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(a, "--observe", StringComparison.OrdinalIgnoreCase)) return;
        }
        // Launched from an existing console (a developer typing into PowerShell): that window is the
        // caller's and stays open by itself, so there is nothing to hold.
        if (!OwnsItsConsoleWindow()) return;

        Console.WriteLine();
        Console.WriteLine(exitCode == ExitCodes.Ok
            ? "  Finished. Press Enter to close this window."
            : $"  This run did not finish (exit code {exitCode}). The reason is above. Press Enter to close this window.");
        try
        {
            Console.ReadLine();
        }
        catch
        {
            // A console that cannot be read from is one nobody is watching; closing is right.
        }
    }

    private static async Task<int> RunAsync(string[] rawArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = CommandLineOptions.Parse(rawArgs);

        /**
         * 887 - A BARE DOUBLE-CLICK INSTALLS, because that is what the person doing it wants.
         *
         * The commonest thing anyone does with a downloaded exe is open it. Until now that printed a
         * help screen and exited, so the owner downloaded this three times, opened it each time, and
         * never left 0.5.0 - every paladin:// click kept running the old build. The install was
         * --install, named once in the middle of the examples.
         *
         * Only a TRULY bare run (no arguments at all) is read this way. Anything with an argument -
         * --help, --version, a replay, a paladin:// link - behaves exactly as it always has, which is
         * why this tests rawArgs rather than ShowHelp: ShowHelp is also set by a malformed command
         * line, and someone who typed something wrong is asking for help, not for an install.
         *
         * And the installed copy never installs itself onto itself: StartupAction decides from where
         * this copy is running, before anything is touched.
         */
        if (rawArgs.Length == 0)
        {
            var intent = StartupAction.ForBareRun(ProtocolRegistrar.CurrentExecutablePath(), Installer.InstalledExePath);
            if (intent == StartupIntent.Install)
            {
                using var installLog = new PaladinLog(echoToConsole: false);
                // A double-click has no console to answer prompts from, so it answers them itself.
                var installUi = new ConsoleShieldUi(assumeYes: true);
                var ok = Installer.Install(installLog, installUi);
                if (ok) installUi.Ok(StartupAction.InstalledMessage);
                return ok ? ExitCodes.Ok : ExitCodes.UnexpectedError;
            }
            // Already the installed copy: say what it is, then the usual help.
            Console.WriteLine($"  Paladin Replay Launcher {AppVersion()} is installed and paladin:// links are registered.");
            CommandLineOptions.PrintUsage(AppVersion());
            return ExitCodes.Ok;
        }

        if (options.ShowHelp)
        {
            CommandLineOptions.PrintUsage(AppVersion());
            return ExitCodes.Ok;
        }

        var appRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
        Directory.CreateDirectory(appRoot);

        using var log = new PaladinLog(echoToConsole: options.Verbose)
        {
            MinimumLevel = options.Verbose ? LogLevel.Debug : LogLevel.Info,
        };
        log.AddFile(Path.Combine(appRoot, "Logs", $"paladin-{DateTime.UtcNow:yyyyMMdd}.log"));

        var configPath = options.ConfigPath ?? Path.Combine(appRoot, "config.json");
        var config = LauncherConfig.LoadOrCreate(configPath);
        ApplyOverrides(config, options);

        var ui = new ConsoleShieldUi(options.AssumeYes);
        var store = new SessionStore(appRoot, log);

        log.Info($"Paladin Replay Launcher starting. args: {string.Join(' ', rawArgs)}");
        log.Debug($"App root: {appRoot}");
        log.Debug($"Config:   {configPath}");

        // Keep the browser link handler working, on every ordinary run. A registration
        // pointing at a moved or deleted exe makes paladin:// links silently do nothing,
        // and the only symptom the user sees is that WATCH REPLAY does not respond.
        //
        // Skipped for the commands where healing would fight the user's intent: install
        // registers by itself, and uninstall/unregister are explicit requests to remove it.
        if (options.Command is not (Command.Install or Command.Uninstall or Command.UnregisterProtocol or Command.Help or Command.RaiseWindow))
            Installer.EnsureRegistrationCurrent(log, ui);

        try
        {
            switch (options.Command)
            {
                case Command.Help:
                    CommandLineOptions.PrintUsage(AppVersion());
                    return ExitCodes.Ok;

                case Command.Version:
                    Console.WriteLine($"Paladin Replay Launcher {AppVersion()}");
                    return ExitCodes.Ok;

                case Command.Doctor:
                    return RunDoctor(config, log, ui, store, configPath, appRoot);

                case Command.Install:
                    return Installer.Install(log, ui) ? ExitCodes.Ok : ExitCodes.UnexpectedError;

                case Command.Uninstall:
                    return Installer.Uninstall(log, ui) ? ExitCodes.Ok : ExitCodes.UnexpectedError;

                case Command.RegisterProtocol:
                    return ProtocolRegistrar.Register(ResolveRegistrationTarget(options), log)
                        ? ExitCodes.Ok : ExitCodes.UnexpectedError;

                case Command.UnregisterProtocol:
                    return ProtocolRegistrar.Unregister(log) ? ExitCodes.Ok : ExitCodes.UnexpectedError;

                case Command.Observe:
                {
                    var providers = new ReplayProviderRegistry();
                    using var observeCts = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; observeCts.Cancel(); };
                    return await new SessionRunner(config, log, ui, store, providers)
                        .RunObserveAsync(observeCts.Token);
                }

                case Command.Recover:
                    return new RecoveryRunner(config, log, ui, store).RunPending(interactive: !options.AssumeYes) == 0
                        ? ExitCodes.Ok : ExitCodes.RestoreFailed;

                case Command.RaiseWindow:
                {
                    if (options.RaiseWindowPid <= 0) { ui.Fail("--raise-window needs a process id."); return ExitCodes.BadArguments; }
                    var raised = await GameWindow.KeepInFrontAsync(
                        options.RaiseWindowPid, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), 1, log, msg => ui.Ok(msg), CancellationToken.None);
                    if (raised == 0) ui.Fail($"Could not bring pid {options.RaiseWindowPid} to the front.");
                    return raised > 0 ? ExitCodes.Ok : ExitCodes.UnexpectedError;
                }

                case Command.Launch:
                    return await RunLaunch(options, config, log, ui, store);

                case Command.Dump:
                    return await RunDump(options, config, log, ui, store);

                case Command.Deep:
                    return await RunDeep(options, config, log, ui, store);

                case Command.DumpUpload:
                    return await RunDumpUpload(options, config, log, ui, store);

                default:
                    CommandLineOptions.PrintUsage(AppVersion());
                    return ExitCodes.BadArguments;
            }
        }
        catch (Exception ex)
        {
            log.Error("Fatal error", ex);
            ui.Fail($"Fatal error: {ex.Message}");
            ui.Note($"Log: {Path.Combine(appRoot, "Logs")}");
            return ExitCodes.UnexpectedError;
        }
    }

    private static async Task<int> RunLaunch(
        CommandLineOptions options, LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store)
    {
        // Crash recovery always runs first: a user who reboots after a crash and clicks
        // another replay must get their old settings back before a new snapshot is taken,
        // otherwise the new snapshot would bake in the damaged state.
        new RecoveryRunner(config, log, ui, store).RunPending(interactive: !options.AssumeYes);

        var request = ResolveRequest(options, ui);
        if (request is null) return ExitCodes.BadArguments;

        var providers = new ReplayProviderRegistry()
            .Register(new LocalReplayProvider(log))
            .Register(new DirectUrlReplayProvider(log));

        if (!providers.Providers.Any(p => p.CanHandle(request)))
        {
            ui.Fail($"No replay provider handles '{request.Kind}' yet.");
            ui.Note($"Available now: {string.Join(", ", providers.Providers.Select(p => p.Id))}.");
            ui.Note("Archive providers (for example aoe4replays) are a planned addition — see the README.");
            return ExitCodes.ReplayUnavailable;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // handle it ourselves so the restore still runs
            ui.Warn("Stopping — Paladin Shield will still check and restore your settings.");
            cts.Cancel();
        };

        var runner = new SessionRunner(config, log, ui, store, providers) { DryRun = options.DryRun };
        return await runner.RunAsync(request, cts.Token);
    }

    /// <summary>
    /// "Dump this game" (§717 §3.1, §3.3): start the replay, read the map out of the
    /// game's own console and send those rows to Paladin. The run itself is
    /// <see cref="DumpRunner"/>; this resolves the request and routes Ctrl+C into the
    /// restore exactly as a watch does.
    /// </summary>
    private static async Task<int> RunDump(
        CommandLineOptions options, LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store)
    {
        // A crashed earlier session is put right before a new snapshot is taken, as for a watch.
        new RecoveryRunner(config, log, ui, store).RunPending(interactive: !options.AssumeYes);

        var request = ResolveDumpRequest(options, config, log, ui);
        if (request is null) return ExitCodes.BadArguments;

        var providers = new ReplayProviderRegistry()
            .Register(new LocalReplayProvider(log))
            .Register(new DirectUrlReplayProvider(log));

        if (!providers.Providers.Any(p => p.CanHandle(request.Replay)))
        {
            ui.Fail($"No replay provider handles '{request.Replay.Kind}' yet, so this game cannot be dumped.");
            ui.Note($"Available now: {string.Join(", ", providers.Providers.Select(p => p.Id))}.");
            return ExitCodes.ReplayUnavailable;
        }

        var runner = new DumpRunner(config, log, ui, store, providers, AppVersion()) { DryRun = options.DryRun };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;   // handle it ourselves so the restore still runs
            // Before the Shield has a snapshot — the pre-flight, the countdown — there is
            // nothing to put back, and promising a restore of nothing is one more thing
            // for the user to wonder about.
            ui.Warn(runner.SessionStarted
                ? "Stopping — Paladin Shield will still check and restore your settings."
                : "Stopping — nothing has been launched, so there is nothing to put back.");
            cts.Cancel();
        };

        var exit = await runner.RunAsync(request, cts.Token);

        // "Dump, then watch": the dump ends its own game as soon as the evidence is safe
        // (D1 amended), so the watch is a second, ordinary session — same replay, same
        // Shield — and only after a dump that actually worked.
        if (request.ThenWatch && exit == ExitCodes.Ok && !options.DryRun && !cts.IsCancellationRequested)
        {
            ui.Note("Now starting the replay again to watch it.");
            var watch = new SessionRunner(config, log, ui, store, providers);
            return await watch.RunAsync(request.Replay, cts.Token);
        }
        if (request.ThenWatch && exit != ExitCodes.Ok)
            ui.Note("The replay was not started again to watch: the dump did not finish.");

        return exit;
    }

    private static async Task<int> RunDumpUpload(
        CommandLineOptions options, LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store)
    {
        if (string.IsNullOrWhiteSpace(options.DumpUploadPath))
        {
            ui.Fail("--dump-upload needs the session id, or the folder an earlier dump kept its rows in.");
            return ExitCodes.BadArguments;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var runner = new DumpRunner(config, log, ui, store, new ReplayProviderRegistry(), AppVersion());
        return await runner.ReSendAsync(options.DumpUploadPath, cts.Token);
    }

    /// <summary>
    /// §867 — "Deep Capture": play the replay and read what every villager is doing, then send that to
    /// Paladin. The request is resolved exactly as a dump's is — the two links name the same two things —
    /// and Ctrl+C routes into the restore the same way.
    /// </summary>
    private static async Task<int> RunDeep(
        CommandLineOptions options, LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store)
    {
        new RecoveryRunner(config, log, ui, store).RunPending(interactive: !options.AssumeYes);

        var request = ResolveDumpRequest(options, config, log, ui, PaladinUri.DeepAction);
        if (request is null) return ExitCodes.BadArguments;

        var providers = new ReplayProviderRegistry()
            .Register(new LocalReplayProvider(log))
            .Register(new DirectUrlReplayProvider(log));

        if (!providers.Providers.Any(p => p.CanHandle(request.Replay)))
        {
            ui.Fail($"No replay provider handles '{request.Replay.Kind}' yet, so this game cannot be captured.");
            ui.Note($"Available now: {string.Join(", ", providers.Providers.Select(p => p.Id))}.");
            return ExitCodes.ReplayUnavailable;
        }

        var runner = new DeepRunner(config, log, ui, store, providers, AppVersion()) { DryRun = options.DryRun };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            ui.Warn(runner.SessionStarted
                ? "Stopping — Paladin Shield will still check and restore your settings."
                : "Stopping — nothing has been launched, so there is nothing to put back.");
            cts.Cancel();
        };

        return await runner.RunAsync(request, cts.Token);
    }

    /// <summary>The dump's request, from a paladin://dump link or from --dump &lt;id&gt; with a replay.</summary>
    private static DumpRequest? ResolveDumpRequest(
        CommandLineOptions options, LauncherConfig config, PaladinLog log, IShieldUi ui, string action = PaladinUri.DumpAction)
    {
        if (options.NoDev)
        {
            // F12: the developer console only exists under -dev.
            ui.Fail($"A {(action == PaladinUri.DeepAction ? "Deep Capture" : "dump")} needs the -dev launch; remove --no-dev.");
            return null;
        }

        long gameId;
        ReplayRequest replay;
        var thenWatch = false;
        var linkForce = false;

        if (options.PaladinUri is not null)
        {
            var parsed = PaladinUri.Parse(options.PaladinUri);
            if (!parsed.Ok)
            {
                ui.Fail($"That paladin:// link could not be used: {parsed.Error}");
                return null;
            }
            if (parsed.Action != action || parsed.GameId is null || parsed.Request is null)
            {
                ui.Fail($"That is not a paladin://{action} link.");
                return null;
            }
            gameId = parsed.GameId.Value;
            replay = parsed.Request;
            thenWatch = parsed.ThenWatch;
            // 884 - a link may ask to re-capture a game Paladin already holds, and it means exactly
            // what `--force` means. The site sends it only for a PARTIAL package, so this cannot
            // quietly re-capture a finished game; the flag is OR-ed with the command-line one so a
            // link and a flag do not fight.
            linkForce = parsed.Force;
        }
        else
        {
            if (options.DumpGameId is not long id)
            {
                ui.Fail($"--{action} needs the game's id (up to {PaladinUri.MaxGameIdDigits} digits), got '{options.DumpGameInput ?? ""}'.");
                return null;
            }
            gameId = id;

            // Without --replay the replay already in the playback folder is used: the owner's
            // queue downloads AgeIV_Replay_<id> there once and dumps it again and again.
            replay = options.ReplayInput is not null
                ? ReplayProviderRegistry.ClassifyRawInput(options.ReplayInput)
                : new ReplayRequest { Kind = "local", Value = PlaybackReplayPath(config, log, gameId) };
        }

        return new DumpRequest(
            gameId, replay,
            Upload: !options.NoUpload,
            Squads: options.Squads,
            Force: options.Force || linkForce,
            AssumeYes: options.AssumeYes,
            ThenWatch: thenWatch || options.ThenWatch);
    }

    /// <summary>playback\AgeIV_Replay_&lt;id&gt;, the file the owner's queue already downloaded.</summary>
    private static string PlaybackReplayPath(LauncherConfig config, PaladinLog log, long gameId)
    {
        var playback = Aoe4Locator.Detect(config, log).PlaybackPath ?? config.PlaybackSubfolder;
        return Path.Combine(playback, $"AgeIV_Replay_{gameId}");
    }

    private static ReplayRequest? ResolveRequest(CommandLineOptions options, IShieldUi ui)
    {
        if (options.PaladinUri is not null)
        {
            var parsed = PaladinUri.Parse(options.PaladinUri);
            if (!parsed.Ok)
            {
                ui.Fail($"That paladin:// link could not be used: {parsed.Error}");
                return null;
            }
            if (parsed.IsDump)
            {
                // Routed to RunDumpStub by the command line; this guards the day that routing changes.
                ui.Fail("That is a paladin://dump link, not a replay to watch.");
                return null;
            }
            return parsed.Request;
        }

        if (options.ReplayInput is not null)
            return ReplayProviderRegistry.ClassifyRawInput(options.ReplayInput);

        ui.Fail("No replay was supplied.");
        CommandLineOptions.PrintUsage(AppVersion());
        return null;
    }

    private static int RunDoctor(
        LauncherConfig config, PaladinLog log, IShieldUi ui, SessionStore store, string configPath, string appRoot)
    {
        Console.WriteLine();
        Console.WriteLine("  Paladin Replay Launcher — environment check");
        Console.WriteLine("  " + new string('-', 46));

        var env = Aoe4Locator.Detect(config, log);
        Report(ui, "Steam", env.SteamExePath);
        Report(ui, "AoE4 install", env.Aoe4InstallDir);
        Report(ui, "AoE4 game exe", env.Aoe4GameExePath);
        if (env.Aoe4GameBuild is not null)
            ui.Ok($"{"AoE4 build",-16} {env.Aoe4GameBuild} (from {env.Aoe4GameVersion})");
        else if (env.Aoe4GameExePath is not null)
            ui.Warn($"{"AoE4 build",-16} could not be read, so replay build checks will be skipped");
        Report(ui, "AoE4 Documents", env.Aoe4DocumentsPath);
        Report(ui, "Playback folder", env.PlaybackPath);
        foreach (var problem in env.Problems) ui.Warn(problem);

        ui.Note($"Config:   {configPath}");
        ui.Note($"Sessions: {store.SessionsRoot}");
        ui.Note($"Logs:     {Path.Combine(appRoot, "Logs")}");

        var registered = ProtocolRegistrar.IsRegistered(out var command);
        var registeredExe = Paladin.Core.Protocol.ShellCommand.ExtractExePath(command);
        if (!registered)
            ui.Warn("paladin:// is not registered. Run with --install.");
        else if (registeredExe is null || !File.Exists(registeredExe))
            ui.Fail($"paladin:// points at a file that no longer exists ({registeredExe ?? "unreadable"}). Run with --repair or --install.");
        else
            ui.Ok($"paladin:// registered -> {registeredExe}");

        if (Installer.IsRunningFromInstallDirectory())
            ui.Ok($"Installed location  {Installer.InstallDirectory}");
        else
            ui.Warn($"Not installed. This copy is running from {ProtocolRegistrar.CurrentExecutablePath()}. Run --install to copy it somewhere Windows will not tidy away.");

        Console.WriteLine();
        Console.WriteLine("  Protected patterns (relative to the AoE4 Documents folder):");
        foreach (var pattern in config.EffectiveProtectedPatterns()) Console.WriteLine($"    + {pattern}");
        Console.WriteLine("  Excluded:");
        foreach (var pattern in config.ExcludedPatterns) Console.WriteLine($"    - {pattern}");

        if (env.CanProtect)
        {
            var policy = new ProtectionPolicy(config.EffectiveProtectedPatterns(), config.ExcludedPatterns);
            var snapshot = new SnapshotService(policy, log, config.MaxProtectedFileBytes, config.VolatileKeys).Capture(env.Aoe4DocumentsPath!);
            Console.WriteLine();
            Console.WriteLine($"  Files that would be protected right now ({snapshot.Count}):");
            foreach (var entry in snapshot)
                Console.WriteLine($"    {entry.SizeBytes,10:N0}  {entry.Sha256[..12]}  {entry.RelativePath}");
        }

        var pending = store.FindPendingRestores(SessionRunner.CurrentUserSid(), Environment.MachineName);
        Console.WriteLine();
        if (pending.Count == 0) ui.Ok("No sessions are pending restore.");
        else
        {
            ui.Warn($"{pending.Count} session(s) pending restore. Run with --recover.");
            foreach (var session in pending) ui.Note($"{session.SessionId}  started {session.StartedUtc:u}  backup: {session.BackupPath}");
        }

        Console.WriteLine();
        return env.CanProtect && env.CanLaunch ? ExitCodes.Ok : ExitCodes.EnvironmentNotFound;
    }

    /// <summary>Informational version from the assembly, so it can never drift from the build.</summary>
    internal static string AppVersion() =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "unknown";

    private static void Report(IShieldUi ui, string label, string? value)
    {
        if (value is not null) ui.Ok($"{label,-16} {value}");
        else ui.Fail($"{label,-16} not found");
    }

    private static string ResolveRegistrationTarget(CommandLineOptions options)
    {
        if (options.RegisterTarget is not null) return Path.GetFullPath(options.RegisterTarget);

        var path = ProtocolRegistrar.CurrentExecutablePath();
        // Under `dotnet run` the host is dotnet.exe, which is not a useful handler target.
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  Note: running under `dotnet run`, so the registered handler would be dotnet.exe.");
            Console.WriteLine("  Publish first, then register the published exe:");
            Console.WriteLine("    PaladinReplayLauncher.exe --register-protocol");
        }
        return path;
    }

    private static void ApplyOverrides(LauncherConfig config, CommandLineOptions options)
    {
        if (options.NoDev) config.UseDevFlag = false;
        if (options.ForceDev) config.UseDevFlag = true;
        if (options.KeepReplay) config.CleanUpPreparedReplay = false;
        if (options.ProtectExtended) config.ProtectExtended = true;
    }
}
