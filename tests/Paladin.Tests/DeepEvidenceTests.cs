using Paladin.Core.Dump;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// §842 — THE DEEP EVIDENCE FILTER, and the silent failure it exists to prevent.
///
/// The dump's own filter admits only PALADIN2 or PALADIN2_*/PALADIN3_*. The Deep sampler prints
/// PALADIN5_VA rows. A Deep capture reusing the dump filter unchanged would therefore keep the
/// framing and ZERO rows — and say nothing about it, because the sampler's completion sentinel is
/// PALADIN3_SQDEF, which DOES match, so the ladder would be judged a success and an empty capture
/// would be uploaded.
///
/// The site's receiving end is already written and pinned against this: src/lib/deepCapture.ts
/// extracts on /PALADIN[567]_[A-Z0-9_]*\|/, and src/lib/deepCapture.test.ts asserts that the dump
/// filter drops these rows today. These tests are the launcher half of that pair.
///
/// The fixtures are real sampler output shapes from the owner's own dump kit, with the console's
/// clock prefix left on exactly as the game writes it.
/// </summary>
public static class DeepEvidenceTests
{
    /// <summary>A real console line, prefix and all, as the game writes it into warnings.log.</summary>
    private static string Line(string message) => $"(I) [00:29:59.218] [000041568]: {message}";

    public static void Register()
    {
        Suite("Deep evidence");

        Test("a representative PALADIN5_VA row survives extraction", () =>
        {
            // Verbatim shape from the banked captures: t|owner|squadId|res|carry×4|idle|moving|hold|blueprint
            var row = Line("PALADIN5_VA|84|1|50051|food|17.0|0.0|0.0|0.0|false|false|-|unit_villager_1_fre_ha_01");
            True(DumpLogText.IsDeepLine(row), "the sampler's own row must be kept");
            Equal("PALADIN5_VA", DumpLogText.DeepMarker(row), "and its marker must be read back");
            False(DumpLogText.IsPaladinLine(row), "THE DUMP FILTER STILL DROPS IT — that is the bug this guards");
        });

        Test("the sampler's framing lines survive too", () =>
        {
            foreach (var marker in new[] { "PALADIN5_VA_BEGIN|84", "PALADIN5_VA_STAT|84|22|0", "PALADIN5_VA_END|84" })
            {
                var line = Line(marker);
                True(DumpLogText.IsDeepLine(line), $"{marker} must be kept");
            }
        });

        Test("THE SENTINEL IS A PALADIN3 LINE, so a Deep capture keeps the dump's markers as well", () =>
        {
            // This is why IsDeepEvidenceLine is a union rather than a replacement: the sampler ends on
            // the squad ladder's own sentinel, and the session's hello/env are the same evidence they
            // are for a dump.
            var sentinel = Line("PALADIN3_SQDEF|allok");
            False(DumpLogText.IsDeepLine(sentinel), "it is not a PALADIN5 line");
            True(DumpLogText.IsPaladinLine(sentinel), "but the dump filter knows it");
            True(DumpLogText.IsDeepEvidenceLine(sentinel), "and a Deep capture must keep it");
        });

        Test("ordinary game-log content is rejected", () =>
        {
            var rejected = new[]
            {
                Line("Loading step: [Assign Players]"),
                Line("GAME -- Human Player: 1 SomeName 1234 0 french"),
                Line("Type checksum mismatch for executable type checksum 356162917"),
                Line("sucessfully loaded art\\civilizations\\common\\weapons\\projectiles\\common_arrow"),
                Line("Application crashed."),
                Line("D3DLOG Begin"),
                "RelicCardinal.exe has stopped",
                "",
            };
            foreach (var line in rejected)
            {
                False(DumpLogText.IsDeepEvidenceLine(line), $"must be rejected: {line}");
            }
        });

        Test("a marker QUOTED inside a message is not a marker", () =>
        {
            // The anchor is the whole point: a fatal error that happens to quote a row must not be
            // mistaken for one. Same rule the dump filter already keeps.
            var quoted = Line("ERROR: unexpected token near 'PALADIN5_VA|84|1|50051|food'");
            False(DumpLogText.IsDeepLine(quoted), "a quoted marker is not an anchored one");
        });

        Test("a lowercase or malformed marker is not admitted", () =>
        {
            False(DumpLogText.IsDeepLine(Line("paladin5_va|84|1")), "case matters");
            False(DumpLogText.IsDeepLine(Line("PALADIN5_VA 84 1")), "the pipe is required");
            False(DumpLogText.IsDeepLine(Line("PALADIN4_VA|84|1")), "4 is not a Deep sampler");
            False(DumpLogText.IsDeepLine(Line("PALADIN8_VA|84|1")), "nor is 8");
        });

        Test("6 and 7 are admitted, so a later sampler does not need this file edited", () =>
        {
            True(DumpLogText.IsDeepLine(Line("PALADIN6_VA|84|1")), "reserved for a later sampler");
            True(DumpLogText.IsDeepLine(Line("PALADIN7_VA|84|1")), "reserved for a later sampler");
        });

        Test("A WHOLE SESSION REDUCES TO ITS EVIDENCE, and the rows are not lost", () =>
        {
            var session = string.Join("\n", new[]
            {
                Line("Loading step: [Mod Packs]"),
                Line("PALADIN3_HELLO|1"),
                Line("GAME -- Human Player: 2 Other 99 1 rus"),
                Line("PALADIN5_VA_BEGIN|84"),
                Line("PALADIN5_VA|84|1|50051|food|17.0|0.0|0.0|0.0|false|false|-|unit_villager_1_fre"),
                Line("PALADIN5_VA|84|2|50052|wood|0.0|11.0|0.0|0.0|false|true|-|unit_villager_1_byz"),
                Line("D3DLOG End"),
                Line("PALADIN5_VA_END|84"),
                Line("PALADIN3_SQDEF|allok"),
                Line("Application crashed."),
            });

            var kept = DumpEvidence.DeepLines(session);
            Equal(6, kept.Count, "hello + begin + two rows + end + sentinel");
            Equal(2, kept.Count(l => DumpLogText.DeepMarker(l) == "PALADIN5_VA"), "both rows survive");
            False(kept.Any(l => l.Contains("Human Player")), "and nothing that is not Paladin's business leaves");
            False(kept.Any(l => l.Contains("D3DLOG")), "nor the crash reporter's own chatter");

            // The dump's filter on the same session finds the framing and NONE of the rows: the exact
            // shape of the silent failure.
            var asDump = DumpEvidence.PaladinLines(session);
            Equal(2, asDump.Count, "hello + sentinel only");
            Equal(0, asDump.Count(l => l.Contains("PALADIN5_VA")), "ZERO rows — an empty capture that looked like a success");
        });

        Test("the clock prefix is kept, because the site's extractor finds the marker rather than anchoring it", () =>
        {
            var row = Line("PALADIN5_VA|84|1|50051|food|17.0|0.0|0.0|0.0|false|false|-|unit_villager_1_fre");
            var kept = DumpEvidence.DeepLines(row);
            Equal(1, kept.Count);
            Equal(row, kept[0], "verbatim, prefix and all");
        });
    }
}
