using Paladin.Core.Config;

namespace Paladin.Core.Steam;

/// <summary>
/// Builds the Steam command line. Separated from the process-start code so the exact
/// argument string can be asserted in tests — including the Phase 4 question of whether
/// -dev is required.
/// </summary>
public static class LaunchCommandBuilder
{
    /// <summary>
    /// The name AoE4 is given for the replay. Downloaded replays observed on a live
    /// install sit in playback/ with NO extension, which is the convention the working
    /// aoe4replays.gg command uses, so the extension is stripped by default.
    /// </summary>
    public static string ReplayFileNameFor(string sourceFileName, bool stripExtension)
    {
        var name = Path.GetFileName(sourceFileName);
        return stripExtension ? Path.GetFileNameWithoutExtension(name) : name;
    }

    public static string BuildReplayArgument(LauncherConfig config, string replayFileNameInPlaybackFolder) =>
        config.ReplayArgumentTemplate.Replace("{name}", replayFileNameInPlaybackFolder);

    public static string BuildSteamArguments(LauncherConfig config, string replayFileNameInPlaybackFolder)
    {
        var replayArg = BuildReplayArgument(config, replayFileNameInPlaybackFolder);

        var args = config.LaunchArgumentTemplate
            .Replace("{appid}", config.SteamAppId.ToString())
            .Replace("{devflag}", config.UseDevFlag ? "-dev" : "")
            .Replace("{replayarg}", replayArg);

        return CollapseSpaces(args);
    }

    /// <summary>
    /// Direct-exe fallback for when Steam cannot be located. Steam is strongly preferred:
    /// launching RelicCardinal.exe directly bypasses Steam's own startup and is untested.
    /// </summary>
    public static string BuildDirectGameArguments(LauncherConfig config, string replayFileNameInPlaybackFolder)
    {
        var replayArg = BuildReplayArgument(config, replayFileNameInPlaybackFolder);
        var dev = config.UseDevFlag ? "-dev " : "";
        return CollapseSpaces($"{dev}-replay {replayArg}");
    }

    private static string CollapseSpaces(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
