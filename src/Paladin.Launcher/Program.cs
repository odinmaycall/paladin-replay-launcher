using System.Reflection;
using System.Runtime.Versioning;
using Paladin.Core.Config;
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

    private static async Task<int> Main(string[] rawArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = CommandLineOptions.Parse(rawArgs);
        if (options.ShowHelp)
        {
            CommandLineOptions.PrintUsage();
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
        if (options.Command is not (Command.Install or Command.Uninstall or Command.UnregisterProtocol or Command.Help))
            Installer.EnsureRegistrationCurrent(log, ui);

        try
        {
            switch (options.Command)
            {
                case Command.Help:
                    CommandLineOptions.PrintUsage();
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

                case Command.Launch:
                    return await RunLaunch(options, config, log, ui, store);

                default:
                    CommandLineOptions.PrintUsage();
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
            return parsed.Request;
        }

        if (options.ReplayInput is not null)
            return ReplayProviderRegistry.ClassifyRawInput(options.ReplayInput);

        ui.Fail("No replay was supplied.");
        CommandLineOptions.PrintUsage();
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
