using Paladin.Core.Logging;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

public static class PathDisplayTests
{
    private const string Profile = @"C:\Users\Kyle";

    public static void Register()
    {
        Suite("PathDisplay");

        Test("a redirected Documents folder never shows its name or the username", () =>
        {
            var shown = PathDisplay.ForConsole(@"C:\Users\Kyle\OneDrive\Tax Return (copy docs)\My Games\Age of Empires IV", Profile);
            True(shown == @"~\...\My Games\Age of Empires IV", $"got '{shown}'");
        });

        Test("a short path under the profile is shown whole behind the tilde", () =>
        {
            var shown = PathDisplay.ForConsole(@"C:\Users\Kyle\Documents\My Games", Profile);
            True(shown == @"~\Documents\My Games", $"got '{shown}'");
        });

        Test("a path outside the profile keeps its drive and its last two folders", () =>
        {
            var shown = PathDisplay.ForConsole(@"D:\Games\Steam\steamapps\common\Age of Empires IV", Profile);
            True(shown == @"D:\...\common\Age of Empires IV", $"got '{shown}'");
        });

        Test("the profile match is case-insensitive and forward slashes are normalised", () =>
        {
            var shown = PathDisplay.ForConsole("c:/users/KYLE/OneDrive/Docs/My Games/Age of Empires IV/", Profile);
            True(shown == @"~\...\My Games\Age of Empires IV", $"got '{shown}'");
        });

        Test("no profile known: the drive and the last two folders", () =>
        {
            var shown = PathDisplay.ForConsole(@"C:\Users\Kyle\OneDrive\Docs\My Games\Age of Empires IV", null);
            True(shown == @"C:\...\My Games\Age of Empires IV", $"got '{shown}'");
        });

        Test("the profile itself, an empty path and a bare name pass through sensibly", () =>
        {
            True(PathDisplay.ForConsole(Profile, Profile) == "~", "the profile is the tilde");
            True(PathDisplay.ForConsole("", Profile) == "", "empty stays empty");
            True(PathDisplay.ForConsole("playback", Profile) == "playback", "a bare name is itself");
        });
    }
}
