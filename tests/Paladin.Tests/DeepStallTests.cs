using Paladin.Core.Deep;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// 930 - A REPLAY THAT PLAYS BUT DOES NOT ADVANCE.
///
/// Measured on 30 Sept and 8 Oct 2026: seven captures ran the whole window and were refused at the
/// Worker as "stalled" (the same villager count on every side for 90s). The same games stalled again on
/// a second night, so it is the replay, not luck. On 8 Oct the game reported sim rate 64 and a clock
/// running at 8x, and the villagers carried wood from sample to sample, so comparing whole rows missed
/// the very case it was written for. The signal is the Worker's: the villager count per side.
/// </summary>
public static class DeepStallTests
{
    private static string Prefix(int n) => $"22:36:{52 + n / 10:00}.{n % 10:000}   ";

    /// <summary>One sample: <paramref name="mine"/> and <paramref name="theirs"/> villagers, each carrying <paramref name="carry"/>.</summary>
    private static IEnumerable<string> Sample(int clock, int mine, int theirs, string carry = "0.0")
    {
        for (var i = 0; i < mine; i++)
            yield return Prefix(clock) + $"PALADIN6_VA|{clock}|1|{50060 + i}|wood|{carry}|0.0|0.0|0.0|false|false|-|false|63|129|unit_villager_1_mon";
        for (var i = 0; i < theirs; i++)
            yield return Prefix(clock) + $"PALADIN6_VA|{clock}|2|{50080 + i}|-|{carry}|0.0|0.0|0.0|true|false|-|false|-70|-127|unit_villager_1_hre";
    }

    private static List<string> Run(int samples, Func<int, (int Mine, int Theirs)> counts, Func<int, string>? carry = null)
    {
        var rows = new List<string>();
        for (var i = 0; i < samples; i++)
        {
            var (m, t) = counts(i);
            rows.AddRange(Sample(6 + i * 5, m, t, carry?.Invoke(i) ?? "0.0"));
        }
        return rows;
    }

    public static void Register()
    {
        Suite("Deep stall detection by game seconds");

        Test("the stall limit is 80 game seconds whatever the sampling interval", () =>
        {
            Equal(16, DeepSession.StaticSamplesFor(5), "the production interval is exactly what it always was");
            Equal(27, DeepSession.StaticSamplesFor(3), "80 s at 3 s");
            Equal(80, DeepSession.StaticSamplesFor(1), "80 s at 1 s, where a fixed 16 ended a healthy replay at 8:45");
            Equal(16, DeepSession.StaticSamplesFor(15), "a slower interval never goes below the production count");
            True(DeepSession.StaticSamplesFor(1) * 1 < 90, "still inside the Worker's 90 s, so the launcher decides first");
        });

        Suite("Deep stall detection");

        Test("a villager count that never changes is counted sample by sample, whatever the clock does", () =>
        {
            Equal(16, DeepStall.TrailingUnchangedCounts(Run(16, _ => (6, 6))));
            Equal(40, DeepStall.TrailingUnchangedCounts(Run(40, _ => (6, 6))));
        });

        Test("THE 8 OCT CASE: villagers keep gathering, the rows keep changing, nobody trains one", () =>
        {
            var rows = Run(30, _ => (6, 6), i => (i % 4 * 2.0).ToString("0.0"));
            Equal(30, DeepStall.TrailingUnchangedCounts(rows), "carry changes every sample and the count does not");
        });

        Test("a side that trains even one villager is advancing", () =>
        {
            True(DeepStall.TrailingUnchangedCounts(Run(30, i => (6 + i / 3, 6))) <= 3, "my side grows every third sample, so no run reaches the limit");
        });

        Test("only the trailing run counts, so a replay that stalled late is still caught", () =>
        {
            var rows = Run(30, i => (i < 10 ? 6 + i : 16, 6));
            Equal(20, DeepStall.TrailingUnchangedCounts(rows), "the count last changes at sample 10, then holds for twenty samples");
        });

        Test("a replay that stalled and then recovered is not stalled any more", () =>
        {
            Equal(0, DeepStall.TrailingUnchangedCounts(Run(30, i => (i < 25 ? 6 : 6 + i, 6))));
        });

        Test("a villager dying counts as movement on that side", () =>
        {
            Equal(0, DeepStall.TrailingUnchangedCounts(Run(20, i => (20, i == 19 ? 19 : 20))));
        });

        Test("nothing to compare is not a stall", () =>
        {
            Equal(0, DeepStall.TrailingUnchangedCounts(Array.Empty<string>()));
            Equal(0, DeepStall.TrailingUnchangedCounts(Sample(6, 6, 6)), "one sample cannot be compared with anything");
            Equal(0, DeepStall.TrailingUnchangedCounts(new[] { "22:36:52.106   PALADIN3_SQ_BEGIN|sampler5" }), "framing lines carry no rows");
        });

        Test("the limit sits inside the Worker's ninety seconds, so the launcher decides first", () =>
        {
            Equal(16, DeepSession.StaticSamples);
            True(DeepSession.StaticSamplesFor(DeepLadder.Cadence) * DeepLadder.Cadence < 90, "80s against the Worker's 90s, at the production interval");
        });

        Test("the probe asks for the rate and the clock and changes neither", () =>
        {
            True(DeepLadder.Probe.Contains(DeepLadder.ProbeMarker), DeepLadder.Probe);
            True(DeepLadder.Probe.Contains("Misc_GetSimRate") && DeepLadder.Probe.Contains("World_GetGameTime"), DeepLadder.Probe);
            False(DeepLadder.Probe.Contains("Misc_SetSimRate"), "a probe that sets the rate is not a probe");
        });

        Test("the failure says what happened and that nothing was sent", () =>
        {
            var f = DeepFailures.Stalled(16, 301, "PALADIN9_PROBE|64.0|301");
            Equal("D4", f.Code);
            True(f.Text.Contains("5:01") && f.Text.Contains("Nothing was sent"), f.Text);
        });
    }
}
