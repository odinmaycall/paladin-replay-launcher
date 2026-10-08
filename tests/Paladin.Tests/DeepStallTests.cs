using Paladin.Core.Deep;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// 930 - A CAPTURE WHOSE CLOCK ADVANCES WHILE THE WORLD DOES NOT.
///
/// Measured on the 30 Sept 2026 queue night: four captures ran 915-965s of samples with every villager
/// row identical to the one before (positions, carry, orders), then were refused at the Worker with
/// HTTP 400 "stalled". Each cost a complete capture to discover. The launcher now notices after twelve
/// samples. These fixtures are the real row shape, with the console's clock prefix left on.
/// </summary>
public static class DeepStallTests
{
    private static string Prefix(int n) => $"22:36:{52 + n / 10:00}.{n % 10:000}   ";

    private static IEnumerable<string> Sample(int clock, string x = "63", string carry = "0.0") => new[]
    {
        Prefix(clock) + $"PALADIN6_VA|{clock}|1|50060|-|{carry}|0.0|0.0|0.0|true|false|-|false|{x}|129|unit_villager_1_mon",
        Prefix(clock) + $"PALADIN6_VA|{clock}|2|50072|-|{carry}|0.0|0.0|0.0|true|false|-|false|-70|-127|unit_villager_1_hre",
    };

    private static List<string> Run(int samples, Func<int, string> x)
    {
        var rows = new List<string>();
        for (var i = 0; i < samples; i++) rows.AddRange(Sample(6 + i * 5, x(i)));
        return rows;
    }

    public static void Register()
    {
        Suite("Deep stall detection");

        Test("a world that never changes is counted sample by sample, ignoring the advancing clock", () =>
        {
            Equal(12, DeepStall.TrailingStaticSamples(Run(12, _ => "63")), "twelve identical samples, twelve different clocks");
            Equal(30, DeepStall.TrailingStaticSamples(Run(30, _ => "63")));
        });

        Test("a world that moves is never called stopped", () =>
        {
            Equal(0, DeepStall.TrailingStaticSamples(Run(40, i => (60 + i).ToString())), "every sample differs");
        });

        Test("only the TRAILING run counts, so a world that froze late is still caught", () =>
        {
            var rows = Run(20, i => i < 10 ? (60 + i).ToString() : "70");
            Equal(10, DeepStall.TrailingStaticSamples(rows), "samples 10 to 19 are ten identical ones after the last change");
        });

        Test("a world that froze and then recovered is not stopped any more", () =>
        {
            var rows = Run(20, i => i < 15 ? "63" : (80 + i).ToString());
            Equal(0, DeepStall.TrailingStaticSamples(rows), "the newest sample differs from the one before");
        });

        Test("a changing carry alone is movement", () =>
        {
            var rows = new List<string>();
            for (var i = 0; i < 12; i++) rows.AddRange(Sample(6 + i * 5, "63", (i % 3).ToString("0.0")));
            Equal(0, DeepStall.TrailingStaticSamples(rows));
        });

        Test("nothing to compare is not a stall", () =>
        {
            Equal(0, DeepStall.TrailingStaticSamples(Array.Empty<string>()));
            Equal(0, DeepStall.TrailingStaticSamples(Sample(6)), "one sample cannot be compared with anything");
            Equal(0, DeepStall.TrailingStaticSamples(new[] { "22:36:52.106   PALADIN3_SQ_BEGIN|sampler5" }), "framing lines carry no rows");
        });

        Test("the limit is a minute of game time, far inside the Worker's ninety seconds", () =>
        {
            Equal(12, DeepSession.StaticSamples);
            True(DeepSession.StaticSamples * DeepLadder.Cadence <= 90, "the launcher decides before the Worker would");
        });

        Test("the probe asks for the rate and the clock and changes neither", () =>
        {
            True(DeepLadder.Probe.Contains(DeepLadder.ProbeMarker), DeepLadder.Probe);
            True(DeepLadder.Probe.Contains("Misc_GetSimRate") && DeepLadder.Probe.Contains("World_GetGameTime"), DeepLadder.Probe);
            False(DeepLadder.Probe.Contains("Misc_SetSimRate"), "a probe that sets the rate is not a probe");
        });

        Test("the failure says what happened and that nothing was sent", () =>
        {
            var f = DeepFailures.Stalled(14, 301, "PALADIN9_PROBE|0.0|301");
            Equal("D4", f.Code);
            True(f.Text.Contains("5:01") && f.Text.Contains("Nothing was sent"), f.Text);
        });
    }
}
