using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Paladin.Core.Deep;
using Paladin.Core.Dump;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// §867 — the Deep bootstrap, the gzip envelope and the pre-flight.
///
/// THE ONE THING THESE TESTS EXIST TO PROTECT is the gate: the launcher must not call SQ() until the
/// sampler's own self-check says every helper landed. Paladin measured twenty real launches and four
/// failed, every one of them a dropped paste line — and two of those four ran the full fifteen minutes
/// producing nothing, because the kit commits to sampling in the same paste that defines the sampler.
/// </summary>
public static class DeepLadderTests
{
    private static string Line(string message) => $"(I) [00:29:59.218] [000041568]: {message}";

    public static void Register()
    {
        Suite("Deep ladder");

        Test("the sampler's definitions ship with the launcher and every line is inside the proven cap", () =>
        {
            var defs = DeepLadder.Definitions;
            Equal(15, defs.Count, "the bootstrap's fifteen definition lines");
            True(defs.All(l => l.Length <= DeepLadder.MaxLine), "a line over 411 characters is truncated by the console, which is a fatal syntax error");
            True(defs.Any(l => l.Contains("function SAMPLE5()", StringComparison.Ordinal)), "SAMPLE5 must be defined");
            True(defs.Any(l => l.Contains("function REG5(", StringComparison.Ordinal) || l.Contains("REG5", StringComparison.Ordinal)), "REG5 must be defined");
            True(defs.Any(l => l.Contains("function V5WHO", StringComparison.Ordinal) || l.Contains("V5WHO", StringComparison.Ordinal)), "V5WHO is the self-check and must be defined");
        });

        Test("THE CADENCE AND THE RATE ARE THE PRODUCTION ONES, carried in the sampler itself", () =>
        {
            Equal(5, DeepLadder.Cadence);
            Equal(64, DeepLadder.SimRate);
            Equal(900, DeepLadder.WindowSeconds);
            True(DeepLadder.Definitions.Any(l => l.Contains(",SAMPLE5,5", StringComparison.Ordinal)),
                "the interval literal inside REG5 must be the 5s cadence, not the kit's 15");
            True(DeepLadder.Definitions.Any(l => l.Contains("V9T(64)", StringComparison.Ordinal)),
                "the thaw inside SQ2 must go to rate 64");
        });

        Test("the whole bootstrap is freeze, definitions, self-check, THEN go — in that order", () =>
        {
            var all = DeepLadder.AllLines();
            Equal(18, all.Count, "freeze + 15 definitions + self-check + SQ()");
            True(all[0].Contains(DeepLadder.FreezeMarker, StringComparison.Ordinal), "the freeze is FIRST, so the definitions cost no game time");
            True(all[^2].Contains(DeepLadder.AllOkMarker, StringComparison.Ordinal), "the self-check is second to last");
            Equal("SQ()", all[^1], "and sampling is committed to LAST, only after the check");
            True(all.All(l => l.Length <= DeepLadder.MaxLine), "every line, including the generated ones");
        });

        Test("THE MARKERS THE LAUNCHER WAITS ON ARE RECOGNISED — this cost a real capture", () =>
        {
            // Verbatim from the failed run's game log. The freeze landed at 6.625s and the launcher
            // could not see it, because HasMarker validated against a regex admitting only PALADIN2
            // and PALADIN3. It waited for a line that was already on screen and gave up.
            var freeze = Line("PALADIN9_FREEZE|true|0.0|6.625");
            True(Paladin.Core.Dump.DumpLogText.HasMarker(freeze, DeepLadder.FreezeMarker), "the freeze must be visible to the wait");

            var thaw = Line("PALADIN9_THAW|true|64.0|6.625");
            True(Paladin.Core.Dump.DumpLogText.HasMarker(thaw, DeepLadder.ThawMarker), "and so must the thaw");

            var missing = Line("PALADIN5_MISSING|V5A,V5B,V5P,");
            True(Paladin.Core.Dump.DumpLogText.HasMarker(missing, DeepLadder.MissingMarker), "and the self-check's refusal, or a repair can never be triggered");

            var allok = Line("PALADIN3_SQDEF|allok");
            True(Paladin.Core.Dump.DumpLogText.HasMarker(allok, DeepLadder.AllOkMarker), "and the self-check's success");

            var done = Line("PALADIN3_SQ_DONE|interval5");
            True(Paladin.Core.Dump.DumpLogText.HasMarker(done, DeepLadder.SquadDoneMarker), "and SQ()'s own answer");
        });

        Test("widening the WAIT did not widen the dump's evidence filter", () =>
        {
            // §842 keeps the dump's filter narrow on purpose: a world dump must never start carrying
            // sampler rows. Only the "is this a marker at all" check moved.
            var sample = Line("PALADIN5_VA|84|1|50051|food|17.0|0.0|0.0|0.0|false|false|-|unit_villager_1_fre");
            False(Paladin.Core.Dump.DumpLogText.IsPaladinLine(sample), "the dump filter still drops a sampler row");
            False(Paladin.Core.Dump.DumpLogText.IsPaladinLine(Line("PALADIN9_FREEZE|true|0.0|6.625")), "and still drops the freeze");
        });

        Test("a marker quoted inside a fatal error is still not a marker", () =>
        {
            False(Paladin.Core.Dump.DumpLogText.HasMarker(Line("SCAR error: attempt to call 'PALADIN9_FREEZE|x' (a nil value)"), DeepLadder.FreezeMarker),
                "the anchor at the start of the message is what makes it a marker");
        });

        Test("the freeze stops game time and reports the rate and the clock", () =>
        {
            var freeze = DeepLadder.Freeze;
            True(freeze.Contains("Misc_SetSimRate,0", StringComparison.Ordinal), "rate 0 is what stops game time dead");
            True(freeze.Contains("World_GetGameTime", StringComparison.Ordinal), "and the clock it froze at is the evidence it worked");
        });

        Test("the thaw exists as a line of its own, so a game is never abandoned frozen", () =>
        {
            var thaw = DeepLadder.Thaw;
            True(thaw.Contains("Misc_SetSimRate,64", StringComparison.Ordinal), "the thaw must restore the production rate");
            True(thaw.Contains(DeepLadder.ThawMarker, StringComparison.Ordinal), "and say so, so the launcher can confirm it landed");
            True(thaw.Length <= DeepLadder.MaxLine, "a truncated thaw is a syntax error and the game stays frozen");
        });
    }
}

/// <summary>§867 — reading what the sampler says about itself.</summary>
public static class DeepSelfCheckTests
{
    private static string Line(string message) => $"(I) [00:29:59.218] [000041568]: {message}";

    public static void Register()
    {
        Suite("Deep self-check");

        Test("THE FOUR REAL FAILURES are read back as the helpers that were lost", () =>
        {
            // Verbatim from the measured logs.
            Equal("V5A,V5B,V5P", string.Join(",", DeepLadder.MissingFrom(Line("PALADIN5_MISSING|V5A,V5B,V5P,"))), "245451182 lost line 7");
            Equal("V5H,V5C,V5I,V5M", string.Join(",", DeepLadder.MissingFrom(Line("PALADIN5_MISSING|V5H,V5C,V5I,V5M,"))), "249682481 lost line 6");
            Equal("SQ2", string.Join(",", DeepLadder.MissingFrom(Line("PALADIN5_MISSING|SQ2,"))), "249069259 lost SQ2 itself");
        });

        Test("SAMPLE5's own refusal reports the same names, so either line can drive the repair", () =>
        {
            Equal("V5A,V5B,V5P", string.Join(",", DeepLadder.MissingFrom(Line("PALADIN5_NOFN|V5A,V5B,V5P,"))), "SAMPLE5 names the same helpers");
        });

        Test("allok reports nothing missing", () =>
        {
            Equal(0, DeepLadder.MissingFrom(Line("PALADIN3_SQDEF|allok")).Count);
            Equal(0, DeepLadder.MissingFrom(Line("PALADIN5_VA|7|1|50001|food|0|0|0|0|false|false|-|unit_villager_1_fre")).Count);
            Equal(0, DeepLadder.MissingFrom(null).Count);
            Equal(0, DeepLadder.MissingFrom("").Count);
        });

        Test("a missing V5WHO itself is reported, because the check cannot check itself away", () =>
        {
            Equal("V5WHO", string.Join(",", DeepLadder.MissingFrom(Line("PALADIN5_MISSING|V5WHO"))), "the check reports its own absence");
        });
    }
}

/// <summary>§867 — the gzip envelope, and the cap it exists to clear.</summary>
public static class DeepEnvelopeTests
{
    public static void Register()
    {
        Suite("Deep envelope");

        Test("the envelope is the shape the Worker reads", () =>
        {
            var envelope = DeepEnvelope.From(245009990, new[] { "row one", "row two" });
            using var doc = JsonDocument.Parse(envelope.ToJson());
            Equal(1, doc.RootElement.GetProperty("v").GetInt32());
            Equal(245009990L, doc.RootElement.GetProperty("gameId").GetInt64());
            Equal("row one\nrow two", doc.RootElement.GetProperty("rows").GetString()!);
        });

        Test("THE BLOCKER: a real-sized cadence-5 capture is over 1 MiB raw and fits easily gzipped", () =>
        {
            // The shape and size of the measured captures: 181 samples x ~60 villagers x 2 sides.
            var rows = new List<string>();
            for (var s = 0; s < 181; s++)
                for (var v = 0; v < 120; v++)
                    rows.Add($"(I) [00:29:59.218] [000041568]: PALADIN5_VA|{7 + s * 5}|{1 + v % 2}|{50000 + v}|food|420|310|150|0|false|false|-|unit_villager_1_fre");

            var envelope = DeepEnvelope.From(245009990, rows);
            True(envelope.JsonBytes > 1024 * 1024, $"the fixture must really be over the cap (it is {envelope.JsonBytes:N0} bytes)");

            var gzip = envelope.ToGzip();
            True(gzip.Length < 1024 * 1024, $"and gzipped it fits ({gzip.Length:N0} bytes)");
            True(envelope.JsonBytes / gzip.Length >= 5, $"a capture-shaped body compresses hard (x{envelope.JsonBytes / gzip.Length})");
        });

        Test("the gzip round-trips to byte-identical JSON, because the Worker inflates it and parses it", () =>
        {
            var envelope = DeepEnvelope.From(1, new[] { "a", "b", "c" });
            using var input = new MemoryStream(envelope.ToGzip());
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8);
            Equal(envelope.ToJson(), reader.ReadToEnd());
        });

        Test("the game's own clock prefix is KEPT, because the site's extractor finds the marker rather than anchoring it", () =>
        {
            var line = "(I) [00:29:59.218] [000041568]: PALADIN5_VA|84|1|50051|food|17.0|0.0|0.0|0.0|false|false|-|unit_villager_1_fre";
            True(DeepEnvelope.From(1, new[] { line }).Rows.Contains("[000041568]", StringComparison.Ordinal), "the prefix is part of the evidence");
        });
    }
}

/// <summary>§867 — the pre-flight, so fifteen minutes are never spent on a game Paladin already holds.</summary>
public static class DeepPreflightTests
{
    public static void Register()
    {
        Suite("Deep pre-flight");

        Test("the live Worker's answer is read, including the limits it now publishes", () =>
        {
            // The exact body paladin.odinmaycall.com returns today.
            var p = DeepUploader.ParsePreflight(
                "{\"status\":\"none\",\"match\":true,\"orders\":false,\"uploads\":true,\"window\":900,\"gzip\":true,\"wireBytes\":1048576,\"textBytes\":8388608}");
            Equal("none", p.Status);
            True(p.Match, "Paladin has the match record, so the two sides can be told apart");
            True(p.Gzip, "and it will inflate a compressed body");
            Equal(1048576L, p.WireBytes);
            Equal(8388608L, p.TextBytes);
            False(p.AlreadyHeld, "status none means the slot is free and the capture is worth running");
        });

        Test("a game Paladin already holds is not worth capturing", () =>
        {
            True(DeepUploader.ParsePreflight("{\"status\":\"owner\"}").AlreadyHeld, "Paladin banked this game itself");
            True(DeepUploader.ParsePreflight("{\"status\":\"user\",\"samples\":184}").AlreadyHeld, "someone has already uploaded one");
        });

        Test("AN OLDER WORKER REPORTS NO GZIP, and the launcher must be able to see that", () =>
        {
            var p = DeepUploader.ParsePreflight("{\"status\":\"none\",\"match\":true,\"orders\":true,\"uploads\":true,\"window\":900}");
            Equal("none", p.Status);
            False(p.Gzip, "so a 5s capture would be refused for size, and the launcher can say why");
        });

        Test("an unreadable answer is unknown rather than a crash", () =>
        {
            False(DeepUploader.ParsePreflight("<!doctype html>").Reachable, "the SPA fallback is not an answer");
            False(DeepUploader.ParsePreflight("").Reachable, "an empty body is not an answer");
            False(DeepUploader.ParsePreflight("{}").Reachable, "JSON with no status is not an answer");
        });

        Test("the upload target is the Worker's own route", () =>
        {
            var uploader = new DeepUploader("https://paladin.odinmaycall.com", "0.5.0", new Core.Logging.PaladinLog(false));
            Equal("https://paladin.odinmaycall.com/api/deep/245009990", uploader.TargetFor(245009990).ToString());
            Equal("paladin.odinmaycall.com", uploader.Host);
        });

        Test("THE CONFIGURED ADDRESS IS THE WORLD DUMP'S, so only its ORIGIN may be used", () =>
        {
            // This got as far as launching a game before it was caught: DumpUploadBaseUrl already ends
            // in /api/world/, so appending to it produced /api/world/api/deep/<id> — which the site
            // answers with the SPA fallback, HTML that no pre-flight can read.
            var uploader = new DeepUploader("https://paladin.odinmaycall.com/api/world/", "0.5.0", new Core.Logging.PaladinLog(false));
            Equal("https://paladin.odinmaycall.com/api/deep/245009990", uploader.TargetFor(245009990).ToString(), "the world path must not survive");
        });

        Test("a run pointed at a test deploy still talks to it, so production is never hit by accident", () =>
        {
            var local = new DeepUploader("http://localhost:8787/api/world/", "0.5.0", new Core.Logging.PaladinLog(false));
            Equal("http://localhost:8787/api/deep/1", local.TargetFor(1).ToString());
            Equal("localhost:8787", local.Host);
        });

        Test("an unusable address falls back to production rather than throwing mid-run", () =>
        {
            Equal("https://paladin.odinmaycall.com/", DeepUploader.OriginOf("not a url").ToString());
            Equal("https://paladin.odinmaycall.com/", DeepUploader.OriginOf("").ToString());
            Equal("https://paladin.odinmaycall.com/", DeepUploader.OriginOf(null).ToString());
        });
    }
}
