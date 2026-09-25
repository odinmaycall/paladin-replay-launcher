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


        Suite("Launcher callback");

        Test("the announcement is the two parameters the site reads, and nothing else", () =>
        {
            var url = LauncherCallback.UrlFor("https://paladin.odinmaycall.com/api/world/", "0.5.0")!;
            // 884 - deepRetry joins it: the site asks for that name before offering to complete a partial
            // capture, because doing so needs BOTH force= on the link and 879's replay retention.
            // The comma is URL-encoded, which is correct and which the single-capability case never
            // exercised. URLSearchParams on the site decodes it before splitting (launcherCapability.ts).
            Equal("https://paladin.odinmaycall.com/?launcher=0.5.0&caps=deepCapture%2CdeepRetry", url);
        });

        Test("IT ANNOUNCES TO THE CONFIGURED DEPLOY, so a test run never tells production it is here", () =>
        {
            Equal("http://localhost:8787/?launcher=0.5.0&caps=deepCapture%2CdeepRetry", LauncherCallback.UrlFor("http://localhost:8787/api/world/", "0.5.0")!);
        });

        Test("884 - the build announces deepRetry, which the site asks for before offering a retry", () =>
        {
            // ONE name for two things, because a launcher with only one of them would send a reader
            // through a five-minute capture that cannot finish the job: it must understand force= on
            // the link, AND retain the replay, or the re-capture is another sampler-only artifact.
            True(LauncherCallback.Capabilities.Contains(LauncherCallback.DeepRetry), "deepRetry is announced");
            True(LauncherCallback.Capabilities.Contains(LauncherCallback.DeepCapture), "and deepCapture still is");
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
