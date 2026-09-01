namespace Paladin.Core.Replay;

/// <summary>
/// Compares the game build a replay was recorded on against the build currently
/// installed, so a mismatch can be explained before anything is downloaded or launched.
///
/// Both numbers are directly measurable and were confirmed to be the same value on a
/// live install: the replay header carries a uint16 build at offset 2 (observed 11308),
/// and RelicCardinal.exe reports FileVersion 16.3.<b>11308</b>.0. They match exactly.
///
/// Age of Empires IV refuses to play a replay recorded on a build it no longer matches,
/// with "Due to a recent update, the replay is no longer available" — which appears
/// inside the game, long after the launcher has downloaded a file and started Steam.
/// Saying it up front costs nothing and saves the user guessing.
///
/// Deliberately advisory, never blocking. Relic state that not every patch breaks
/// replays, so a mismatch is a reason to warn, not a reason to refuse.
/// </summary>
public static class BuildCompatibility
{
    public enum Verdict
    {
        /// <summary>One or both builds could not be read; say nothing rather than guess.</summary>
        Unknown,
        Match,
        /// <summary>Replay predates the installed game — the common case just after a patch.</summary>
        ReplayOlder,
        /// <summary>Replay is from a newer build than installed; the game is behind.</summary>
        ReplayNewer,
    }

    public sealed record Result(Verdict Verdict, int? ReplayBuild, int? InstalledBuild, string Message)
    {
        /// <summary>True when the user should be told something before the game starts.</summary>
        public bool ShouldWarn => Verdict is Verdict.ReplayOlder or Verdict.ReplayNewer;
    }

    /// <summary>
    /// Pulls the build out of a Windows file version such as "16.3.11308.0", where the
    /// third component is what the replay header stores. Returns null for anything that
    /// does not parse, so an unexpected version format degrades to "no opinion" rather
    /// than a wrong warning.
    /// </summary>
    public static int? ParseGameBuild(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion)) return null;

        var parts = fileVersion.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return null;

        return int.TryParse(parts[2], out var build) && build > 0 ? build : null;
    }

    public static Result Compare(int? replayBuild, int? installedBuild)
    {
        if (replayBuild is null || installedBuild is null)
            return new Result(Verdict.Unknown, replayBuild, installedBuild,
                "Could not compare game builds, so the replay will simply be tried.");

        if (replayBuild == installedBuild)
            return new Result(Verdict.Match, replayBuild, installedBuild,
                $"Replay and game are both build {replayBuild}.");

        if (replayBuild < installedBuild)
            return new Result(Verdict.ReplayOlder, replayBuild, installedBuild,
                $"This replay was recorded on game build {replayBuild}, and Age of Empires IV is now on {installedBuild}. " +
                "It may refuse to play with \"Due to a recent update, the replay is no longer available\".");

        return new Result(Verdict.ReplayNewer, replayBuild, installedBuild,
            $"This replay was recorded on game build {replayBuild}, which is NEWER than your installed {installedBuild}. " +
            "Let Steam finish updating Age of Empires IV and try again.");
    }

    /// <summary>
    /// What the user can actually do about it. Steam publishes a `previous_live` branch —
    /// "Archive of previous live build", no password — which always holds the build
    /// immediately before the current one. That is exactly the case that appears the day
    /// a patch lands, when every existing replay is suddenly one build behind.
    ///
    /// The launcher does not switch branches itself: Steam offers no supported command or
    /// URL for it, so automating it would mean writing Steam's own appmanifest and forcing
    /// an update. Getting that wrong damages a 48 GB install, which is not a risk a tool
    /// that exists to protect people's files should take on their behalf.
    /// </summary>
    /// <summary>
    /// How far behind a replay can be before `previous_live` is no longer plausibly far
    /// enough. A heuristic, and documented as one: consecutive builds observed on real
    /// replays differed by roughly 90-350 (11214 -> 11308, 10884 -> 11214), while an
    /// eight-month-old replay was 4000+ behind. Suggesting a one-build rollback for a
    /// replay that is many patches old would be confidently wrong advice.
    /// </summary>
    public const int PlausiblyRecentBuildGap = 500;

    public static IReadOnlyList<string> SuggestionsFor(Result result)
    {
        if (result.Verdict == Verdict.ReplayNewer)
            return new[] { "Check for an Age of Empires IV update in Steam, then try again." };

        if (result.Verdict != Verdict.ReplayOlder) return Array.Empty<string>();

        var gap = (result.InstalledBuild ?? 0) - (result.ReplayBuild ?? 0);

        if (gap <= PlausiblyRecentBuildGap)
        {
            return new[]
            {
                "It may still play — not every patch breaks replays. Let it try first.",
                "If it fails, you can roll the game back one build in Steam:",
                "  right-click Age of Empires IV -> Properties -> Betas -> previous_live",
                "  (that branch always holds the build just before the current one)",
                "Switch back to 'None'/public afterwards to play multiplayer again.",
            };
        }

        return new[]
        {
            "It may still play — not every patch breaks replays. Let it try first.",
            "This replay is several patches old, so Steam's previous_live branch —",
            "which only ever holds the single build before the current one — is very",
            "unlikely to go back far enough.",
            "Reconstructing an arbitrary old build needs a dedicated tool; the community",
            "one is github.com/EKYavsil/AoE4-Replay-Launcher (separate project, not ours).",
        };
    }
}
