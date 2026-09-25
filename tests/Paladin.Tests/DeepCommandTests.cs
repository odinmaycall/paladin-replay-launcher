using Paladin.Core.Deep;
using Paladin.Core.Protocol;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// §867 — how a Deep Capture is started, and the ways it must refuse to start.
///
/// Both entry points are here because both are things the owner will actually type or click: the
/// website's `paladin://deep?game=...` link, and `--deep &lt;id&gt;` from a terminal.
/// </summary>
public static class DeepCommandTests
{
    public static void Register()
    {
        Suite("Deep links");

        Test("a paladin://deep link parses into the game and the replay", () =>
        {
            var parsed = PaladinUri.Parse("paladin://deep?game=245009990&url=https%3A%2F%2Fe.com%2Fgame.rec");
            True(parsed.Ok, "the link is well formed");
            True(parsed.IsDeep, "and it is a Deep link");
            False(parsed.IsDump, "which is not a dump link");
            True(parsed.IsCapture, "but it is a capture");
            Equal(PaladinUri.DeepAction, parsed.Action!);
            Equal(245009990L, parsed.GameId!.Value);
            Equal("url", parsed.Request!.Kind);
        });

        Test("a deep link needs a game id, exactly as a dump link does", () =>
        {
            var noGame = PaladinUri.Parse("paladin://deep?url=https%3A%2F%2Fe.com%2Fgame.rec");
            False(noGame.Ok, "without game= there is nothing to store the capture against");
            True(noGame.Error!.Contains("deep", StringComparison.Ordinal), "and the refusal names the action the user used");

            False(PaladinUri.Parse("paladin://deep?game=nope&url=https%3A%2F%2Fe.com%2Fg.rec").Ok, "a game id is digits");
            False(PaladinUri.Parse("paladin://deep?game=245009990").Ok, "and a capture needs a replay to play");
        });

        Test("a local path works too, which is how a banked replay is captured", () =>
        {
            var parsed = PaladinUri.Parse("paladin://deep?game=245009990&path=C%3A%5Ctemp%5CAgeIV_Replay_245009990");
            True(parsed.Ok, "a path is a valid replay source");
            Equal("local", parsed.Request!.Kind);
            Equal(245009990L, parsed.GameId!.Value);
        });

        Test("the action is readable before the full parse, so a bare link routes to the right command", () =>
        {
            Equal("deep", PaladinUri.ActionOf("paladin://deep?game=1&url=https%3A%2F%2Fe.com%2Fx")!);
            Equal("dump", PaladinUri.ActionOf("paladin://dump?game=1&url=https%3A%2F%2Fe.com%2Fx")!);
        });


        // 887 - DOUBLE-CLICKING THE DOWNLOAD INSTALLS IT.
        //
        // Measured on a real afternoon: the owner downloaded the launcher three times, opened it each
        // time, and never left 0.5.0, because opening it printed a help screen and exited. Every
        // paladin:// click kept running the old build and nothing on screen said so.
        Suite("Startup intent");

        const string installedPath = @"C:\Users\Someone\AppData\Local\Programs\PaladinReplayLauncher\PaladinReplayLauncher.exe";

        Test("A DOWNLOAD OPENED FROM THE DOWNLOADS FOLDER INSTALLS, which is the newcomer's first act", () =>
        {
            Equal(StartupIntent.Install, StartupAction.ForBareRun(@"C:\Users\Someone\Downloads\PaladinReplayLauncher (3).exe", installedPath));
        });

        Test("AND THE INSTALLED COPY NEVER INSTALLS ITSELF ONTO ITSELF", () =>
        {
            // The installed copy is also launched bare - by a paladin:// link that fails to parse, or
            // by someone finding it in their programs folder. Copying a running file over itself
            // either fails or half-succeeds, so this is the branch that must never be wrong.
            Equal(StartupIntent.Status, StartupAction.ForBareRun(installedPath, installedPath));
        });

        Test("an upgrade over an older installed copy still reads as an install", () =>
        {
            Equal(StartupIntent.Install, StartupAction.ForBareRun(@"C:\Users\Someone\Downloads\PaladinReplayLauncher.exe", installedPath));
        });

        Test("THE SAME FILE UNDER A DIFFERENT SPELLING IS STILL THE SAME FILE", () =>
        {
            // Windows hands paths back inconsistently. A copy that failed to recognise itself here
            // would try to install over the file it is running from.
            Equal(StartupIntent.Status, StartupAction.ForBareRun(installedPath.ToUpperInvariant(), installedPath));
            Equal(StartupIntent.Status, StartupAction.ForBareRun(@"C:\Users\Someone\AppData\Local\Programs\PaladinReplayLauncher\.\PaladinReplayLauncher.exe", installedPath));
            Equal(StartupIntent.Status, StartupAction.ForBareRun(@"C:\Users\Someone\AppData\Local\Programs\Other\..\PaladinReplayLauncher\PaladinReplayLauncher.exe", installedPath));
        });

        Test("an unknown path installs rather than assuming it is already in place", () =>
        {
            // A single-file publish can report no path at all. Installing again is harmless; wrongly
            // believing it is installed leaves the newcomer exactly where they started.
            Equal(StartupIntent.Install, StartupAction.ForBareRun(null, installedPath));
            Equal(StartupIntent.Install, StartupAction.ForBareRun("", installedPath));
            Equal(StartupIntent.Install, StartupAction.ForBareRun(@"C:\Downloads\x.exe", null));
        });

        Test("the success line tells a newcomer they are done, and where to go", () =>
        {
            True(StartupAction.InstalledMessage.Contains("installed successfully"), StartupAction.InstalledMessage);
            True(StartupAction.InstalledMessage.Contains("return to Paladin"), "and names the next move");
        });

        Suite("Launcher callback");

        Test("the announcement is the two parameters the site reads, and nothing else", () =>
        {
            var url = LauncherCallback.UrlFor("https://paladin.odinmaycall.com/api/world/", "0.5.0")!;
            // 884 - deepRetry joins it: the site asks for that name before offering to complete a
            // partial capture. 890 - and replayRetention joins it as its OWN fact rather than as half
            // of deepRetry's meaning. The commas are URL-encoded, which is correct and which the
            // single-capability case never exercised; URLSearchParams on the site decodes before
            // splitting (launcherCapability.ts).
            //
            // THIS EXACT STRING IS THE CROSS-REPO CONTRACT. The site pins the same one in
            // src/lib/launcherCapability.test.ts, so a rename or a reordering here fails a test in a
            // repo this build never sees, which is the only way two halves in two repos stay honest.
            Equal("https://paladin.odinmaycall.com/?launcher=0.5.0&caps=deepCapture%2CdeepRetry%2CreplayRetention", url);
        });

        Test("IT ANNOUNCES TO THE CONFIGURED DEPLOY, so a test run never tells production it is here", () =>
        {
            Equal("http://localhost:8787/?launcher=0.5.0&caps=deepCapture%2CdeepRetry%2CreplayRetention", LauncherCallback.UrlFor("http://localhost:8787/api/world/", "0.5.0")!);
        });

        Test("890 - THIS BUILD CLAIMS RETENTION SEPARATELY, because 0.5.3 claimed it by implication", () =>
        {
            // 884 made deepRetry mean two things at once: understands force=, and keeps the replay.
            // 0.5.3 then shipped saying that name with retention broken by 888's ordering bug, so the
            // site offered retries that could never complete the package. One name covering two
            // independent facts is what allowed a half-true announcement, so they are separate now.
            True(LauncherCallback.Capabilities.Contains(LauncherCallback.DeepCapture), "deepCapture is announced");
            True(LauncherCallback.Capabilities.Contains(LauncherCallback.DeepRetry), "deepRetry still is");
            True(LauncherCallback.Capabilities.Contains(LauncherCallback.ReplayRetention), "and retention says so itself");

            // A build that could not keep a replay would announce the first two and NOT the third, and
            // the site would show the update message. Proving the shape of that announcement here
            // costs nothing and pins what "withholding a capability" actually looks like on the wire.
            var withoutRetention = new[] { LauncherCallback.DeepCapture, LauncherCallback.DeepRetry };
            Equal(
                "https://paladin.odinmaycall.com/?launcher=0.5.3&caps=deepCapture%2CdeepRetry",
                LauncherCallback.UrlFor("https://paladin.odinmaycall.com/", "0.5.3", withoutRetention)!);
        });

        Test("the world dump's path never survives into the announcement", () =>
        {
            Equal("https://paladin.odinmaycall.com/", LauncherCallback.OriginOf("https://paladin.odinmaycall.com/api/world/"));
            Equal("https://paladin.odinmaycall.com/", LauncherCallback.OriginOf("not a url"));
            Equal("https://paladin.odinmaycall.com/", LauncherCallback.OriginOf("file:///C:/x"));
        });

        Test("a version the site would refuse is never sent", () =>
        {
            // The page refuses anything that is not a plain version, so sending one would announce
            // a launcher that the site then ignores — worse than saying nothing.
            True(LauncherCallback.UrlFor("https://x.test", "") is null, "an empty version");
            True(LauncherCallback.UrlFor("https://x.test", "0.5.0 <script>") is null, "anything with markup in it");
            True(LauncherCallback.UrlFor("https://x.test", new string('9', 33)) is null, "a version longer than the page accepts");
        });

        Test("deepCapture is announced by NAME, so the site never encodes a version number", () =>
        {
            True(LauncherCallback.Capabilities.Contains("deepCapture"), "this build can capture");
            True(LauncherCallback.UrlFor("https://x.test", "0.5.0")!.Contains("caps=deepCapture"), "and says so by name");
        });

    }
}
