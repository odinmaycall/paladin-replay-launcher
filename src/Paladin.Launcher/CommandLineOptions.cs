using Paladin.Core.Protocol;

namespace Paladin.Launcher;

public enum Command { Launch, Observe, Recover, Doctor, Install, Uninstall, RegisterProtocol, UnregisterProtocol, Version, Help,
    /// <summary>Maintenance: bring a process's window to the front (--raise-window pid).</summary>
    RaiseWindow,
}

public sealed class CommandLineOptions
{
    public Command Command { get; private set; } = Command.Help;
    public string? ReplayInput { get; private set; }
    public string? PaladinUri { get; private set; }
    public string? ConfigPath { get; private set; }
    public string? RegisterTarget { get; private set; }
    public bool DryRun { get; private set; }
    public bool NoDev { get; private set; }
    public bool ForceDev { get; private set; }
    public bool KeepReplay { get; private set; }
    public bool ProtectExtended { get; private set; }
    public bool AssumeYes { get; private set; }
    public int RaiseWindowPid { get; private set; }
    public bool Verbose { get; private set; }
    public bool ShowHelp { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var options = new CommandLineOptions();
        if (args.Length == 0)
        {
            options.ShowHelp = true;
            return options;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // A bare paladin:// argument is how Windows invokes us from a browser link.
            if (Paladin.Core.Protocol.PaladinUri.LooksLikePaladinUri(arg))
            {
                options.PaladinUri = arg;
                options.Command = Command.Launch;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "--replay" or "-r":
                    options.ReplayInput = Next(args, ref i);
                    options.Command = Command.Launch;
                    break;

                case "--config":
                    options.ConfigPath = Next(args, ref i);
                    break;

                case "--version":
                    options.Command = Command.Version;
                    break;

                case "--install":
                    options.Command = Command.Install;
                    break;

                case "--uninstall":
                    options.Command = Command.Uninstall;
                    break;

                case "--repair":
                    options.Command = Command.RegisterProtocol;
                    break;

                case "--observe":
                    options.Command = Command.Observe;
                    break;

                case "--recover":
                    options.Command = Command.Recover;
                    break;

                case "--doctor":
                    options.Command = Command.Doctor;
                    break;

                case "--register-protocol":
                    options.Command = Command.RegisterProtocol;
                    break;

                case "--unregister-protocol":
                    options.Command = Command.UnregisterProtocol;
                    break;

                case "--register-target":
                    options.RegisterTarget = Next(args, ref i);
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    break;

                case "--raise-window":
                    options.RaiseWindowPid = int.TryParse(Next(args, ref i), out var pid) ? pid : 0;
                    options.Command = Command.RaiseWindow;
                    break;

                case "--no-dev":
                    options.NoDev = true;
                    break;

                case "--dev":
                    options.ForceDev = true;
                    break;

                case "--keep-replay":
                    options.KeepReplay = true;
                    break;

                case "--protect-extended":
                    options.ProtectExtended = true;
                    break;

                case "--yes" or "-y":
                    options.AssumeYes = true;
                    break;

                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;

                case "--help" or "-h" or "-?" or "/?":
                    options.ShowHelp = true;
                    break;

                default:
                    // A bare positional argument is treated as the replay.
                    if (!arg.StartsWith('-') && options.ReplayInput is null)
                    {
                        options.ReplayInput = arg;
                        options.Command = Command.Launch;
                    }
                    break;
            }
        }

        if (options.Command == Command.Launch && options.ReplayInput is null && options.PaladinUri is null)
            options.ShowHelp = true;

        return options;
    }

    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

    public static void PrintUsage()
    {
        Console.WriteLine(
            """

              Paladin Replay Launcher — opens an Age of Empires IV replay with
              Paladin Shield protecting your game settings.

              USAGE
                PaladinReplayLauncher.exe <replay-path-or-url> [options]
                PaladinReplayLauncher.exe --replay <replay-path-or-url> [options]
                PaladinReplayLauncher.exe "paladin://replay?url=<url-encoded-url>"
                PaladinReplayLauncher.exe --recover
                PaladinReplayLauncher.exe --doctor

              EXAMPLES
                Launch a replay already on disk:
                  PaladinReplayLauncher.exe "C:\replays\AgeIV_Replay_244989270"

                Launch a replay from a direct URL:
                  PaladinReplayLauncher.exe "https://example.com/replays/game.rec"

                Rehearse everything except starting the game:
                  PaladinReplayLauncher.exe "C:\replays\game.rec" --dry-run

                Test whether the -dev flag is actually needed (Phase 4):
                  PaladinReplayLauncher.exe "C:\replays\game.rec" --no-dev

                Install it properly (per-user, no admin needed) — do this first:
                  PaladinReplayLauncher.exe --install

              OPTIONS
                -r, --replay <path|url>   Replay to open.
                    --dry-run             Do everything except launching AoE4.
                    --no-dev              Launch WITHOUT -dev.
                    --dev                 Force -dev on (the default).
                    --keep-replay         Do not delete the replay from playback/ afterwards.
                    --protect-extended    Also protect datastore/ progression files.
                    --install             Copy this program to a stable per-user location
                                          and register paladin:// from there. Run once.
                    --uninstall           Remove the link handler; says where the rest is.
                    --repair              Re-register paladin:// to this exe's location.
                    --observe             Control run: snapshot, wait while YOU launch and
                                          close AoE4 normally, report what changed, ask
                                          before restoring. Launches nothing itself.
                    --recover             Finish any interrupted session's restore, then exit.
                    --doctor              Report what was detected and what would be protected.
                    --register-protocol   Register the paladin:// URI scheme for this user.
                    --unregister-protocol Remove it.
                    --register-target <p> Register a specific exe path instead of this one.
                    --config <path>       Use a specific config.json.
                -y, --yes                 Answer prompts automatically (for scripts).
                -v, --verbose             Echo the full log to the console.
                    --version             Print the version and exit.
                -h, --help                This text.

            """);
    }
}
