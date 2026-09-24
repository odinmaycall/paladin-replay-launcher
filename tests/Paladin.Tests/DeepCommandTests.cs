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

    }
}
