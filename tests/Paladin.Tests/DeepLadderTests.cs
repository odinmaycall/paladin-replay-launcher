using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Paladin.Core.Deep;
using Paladin.Core.Dump;
using Paladin.Core.Protocol;
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

        Test("895 - the sampler prints the PLAYER-LEVEL allocation, which the per-squad rows cannot see", () =>
        {
            /**
             * WHY THIS ROW EXISTS. V5K already asks Squad_IsGatheringResourceType per villager - but
             * that predicate is FALSE while a villager is walking to the resource, which is why a
             * build order shows "unresolved" for a group on its way to a stone outcropping. Measured
             * on game 253123900 at 3:01: nine villagers unresolved in the capture, while the game's
             * own HUD and Player_GetNumGatheringSquads both said nine on stone.
             *
             * The two calls answer different questions. This row carries the second answer, so the
             * reduction has a known total to attribute the walkers to instead of guessing.
             */
            var all = string.Join("\n", DeepLadder.Definitions);

            True(all.Contains("PALADIN5_VP|", StringComparison.Ordinal), "the player row has its own marker");
            True(all.Contains("Player_GetNumGatheringSquads", StringComparison.Ordinal), "and it is the player-level aggregate");

            // THE FIELD ORDER IS THE CONTRACT the site parses: t | playerIndex | food | wood | gold |
            // stone. Those indices are not guessable - 2/8/3/7 - and V5K has used the same four since
            // the sampler was written, so the two must not drift apart.
            True(all.Contains("V5PG(p,2)..\"|\"..V5PG(p,8)..\"|\"..V5PG(p,3)..\"|\"..V5PG(p,7)", StringComparison.Ordinal),
                "food, wood, gold, stone - the order V5C already carries the CARRIED amounts in");

            // Guarded, because a syntax error in that console ends the game and the capture with it.
            True(all.Contains("pcall(Player_GetNumGatheringSquads", StringComparison.Ordinal), "every call is pcall-guarded");

            // And the self-check must cover them, or a paste that drops this line stops being noticed
            // - which is the exact failure V5WHO was written for.
            True(all.Contains("V5Z(\"V5PG\",V5PG)", StringComparison.Ordinal), "V5PG is in the self-check");
            True(all.Contains("V5Z(\"V5VP\",V5VP)", StringComparison.Ordinal), "V5VP is in the self-check");
        });

        Test("the sampler's definitions ship with the launcher and every line is inside the proven cap", () =>
        {
            var defs = DeepLadder.Definitions;
            Equal(16, defs.Count, "the bootstrap's sixteen definition lines (895 added the player aggregate)");
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
            Equal(19, all.Count, "freeze + 16 definitions + self-check + SQ()");
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

        Test("§870 — a replay that has closed ends the watch, instead of waiting out the stall timeout", () =>
        {
            // capture.ps1 has always checked this; the launcher did not, so a game that ended early sat
            // for five idle minutes before the loop gave up on what it already had.
            True(Paladin.Core.Deep.DeepSession.ShouldStopForMissingGame(false, 60), "gone, and past the grace");
            False(Paladin.Core.Deep.DeepSession.ShouldStopForMissingGame(true, 600), "still running: keep watching");
        });

        Test("§870 — but an early snapshot is not believed, because it can miss a game that is alive", () =>
        {
            False(Paladin.Core.Deep.DeepSession.ShouldStopForMissingGame(false, 0), "the session starts the moment the process is seen");
            False(Paladin.Core.Deep.DeepSession.ShouldStopForMissingGame(false, Paladin.Core.Deep.DeepSession.GameGoneGraceSeconds - 1), "just inside the grace");
            True(Paladin.Core.Deep.DeepSession.ShouldStopForMissingGame(false, Paladin.Core.Deep.DeepSession.GameGoneGraceSeconds), "and the grace is inclusive");
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

        Test("THE WORKER'S REAL SUCCESS BODY IS READ AS A SUCCESS — this threw away a working capture", () =>
        {
            // Verbatim from the first real capture: 11,153 readings, 0:06 to 15:06, stored by the
            // Worker — and reported to the user as "Paladin did not accept the map". gameId comes back
            // QUOTED, GameId is a long?, so the whole response failed to deserialise and a 200 "stored"
            // fell through to "unexpected".
            var body = "{\"status\":\"stored\",\"gameId\":\"249485354\",\"samples\":181,\"first\":6,\"last\":906,"
                     + "\"bytes\":45476,\"encoding\":\"gzip\",\"wireBytes\":64764,\"textBytes\":1048898,\"url\":\"/deep/249485354.json\"}";
            var result = Paladin.Core.Dump.DumpUploader.Classify(200, "OK", body);
            Equal(Paladin.Core.Dump.DumpUploadOutcome.Stored, result.Outcome, "200 with status stored is a success");
            True(result.Accepted, "and the launcher must say so");
        });

        Test("a quoted number anywhere in the answer no longer breaks the whole read", () =>
        {
            var parsed = Paladin.Core.Dump.DumpUploadResponse.TryParse("{\"status\":\"stored\",\"gameId\":\"12345\",\"bytes\":\"678\"}");
            True(parsed is not null, "the response parses");
            Equal("stored", parsed!.Status!);
            Equal(12345L, parsed.GameId!.Value);
        });

        Test("a plain number still parses, so the world dump's own answers are untouched", () =>
        {
            var parsed = Paladin.Core.Dump.DumpUploadResponse.TryParse("{\"status\":\"stored\",\"gameId\":12345,\"n\":3202}");
            True(parsed is not null, "the dump's shape still reads");
            Equal(12345L, parsed!.GameId!.Value);
            Equal(3202, parsed.N!.Value);
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

        // §876 — THE CLOCK THIS MATCH HAD, asked for rather than compiled in.
        //
        // 22% of the tournament set and 18% of the indexed ladder end before 15:00. Until now this
        // launcher held 900 as a constant, so a reader who captured a 10:15 game end to end was told
        // their complete capture had failed. The Worker publishes the answer; these pin the reading of
        // it, in both directions.
        Test("A SHORT GAME'S OWN CLOCK IS READ FROM THE ANSWER", () =>
        {
            var p = DeepUploader.ParsePreflight(
                "{\"status\":\"none\",\"match\":true,\"orders\":false,\"uploads\":true,\"window\":900,\"required\":570,\"duration\":615,\"gzip\":true,\"wireBytes\":1048576,\"textBytes\":8388608}");
            Equal(570, p.RequiredSeconds);
            Equal(570, p.WindowOr(DeepLadder.WindowSeconds), "a 10:15 game is complete at its own end");
        });

        Test("a long game asks for the window, exactly as before", () =>
        {
            var p = DeepUploader.ParsePreflight(
                "{\"status\":\"none\",\"match\":true,\"orders\":false,\"uploads\":true,\"window\":900,\"required\":900,\"duration\":1223,\"gzip\":true}");
            Equal(900, p.WindowOr(DeepLadder.WindowSeconds));
        });

        Test("AN OLDER PALADIN, OR NONE AT ALL, LEAVES THE WINDOW STANDING", () =>
        {
            // A Worker from before §876 publishes `window` and no `required`; that is still better than
            // a number compiled in here, and it is the same 900.
            var older = DeepUploader.ParsePreflight("{\"status\":\"none\",\"match\":true,\"window\":900,\"gzip\":true}");
            Equal(900, older.WindowOr(DeepLadder.WindowSeconds));

            // One that publishes neither, and one we could not reach at all, both fall back.
            Equal(0, DeepUploader.ParsePreflight("{\"status\":\"none\",\"match\":true}").RequiredSeconds);
            Equal(DeepLadder.WindowSeconds, DeepUploader.ParsePreflight("{\"status\":\"none\",\"match\":true}").WindowOr(DeepLadder.WindowSeconds));
            Equal(DeepLadder.WindowSeconds, DeepUploader.ParsePreflight("<!doctype html>").WindowOr(DeepLadder.WindowSeconds));
        });

        Test("a nonsense clock cannot shorten a capture", () =>
        {
            // Zero, negative and missing all mean "use the window": this must never be able to end a
            // capture early on a malformed answer.
            Equal(DeepLadder.WindowSeconds, DeepUploader.ParsePreflight("{\"status\":\"none\",\"required\":0,\"window\":900}").WindowOr(DeepLadder.WindowSeconds));
            Equal(DeepLadder.WindowSeconds, DeepUploader.ParsePreflight("{\"status\":\"none\",\"required\":-5}").WindowOr(DeepLadder.WindowSeconds));
        });

        Test("the reader is told, once, that the capture runs to the end of a short game", () =>
        {
            var line = DeepConsoleText.ShortGame(570);
            True(line.Contains("9:30"), $"the line must name the clock it will run to: {line}");
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

        // 879 - RETAINING THE REPLAY, which is what turns a sampler artifact into a trusted package.
        //
        // The build order's clocks, its Builders column and its landmark placements all come from the
        // REPLAY's order stream, and Microsoft stops serving replays about three months after the
        // game. So the launcher keeps the exact file it just played, at the one moment it is certainly
        // still there, and Paladin parses it with its OWN parser rather than trusting anyone else's.
        // 884 - ONE CLICK COMPLETES A PARTIAL CAPTURE, which until now needed a terminal.
        //
        // A capture whose sampler landed but whose replay was never retained is reported `partial`,
        // and the launcher's own pre-flight refuses a game Paladin already holds. So the partial chip
        // offered a re-capture the launcher would always decline, and the only way through was
        // `--deep <id> --force`. The link may now say force=1, and it means exactly that flag.
        Suite("Deep retry links");

        Test("FORCE=1 ON A DEEP LINK IS THE SAME THING AS --force", () =>
        {
            var parsed = PaladinUri.Parse("paladin://deep?game=253129753&url=https%3A%2F%2Fexample.com%2Fr.rec&force=1");
            True(parsed.Ok, parsed.Error ?? "");
            True(parsed.IsDeep, "still a Deep link");
            Equal(253129753L, parsed.GameId!.Value);
            True(parsed.Force, "and it carries the force the site asked for");
        });

        Test("a link WITHOUT it never forces, so a complete game is never re-captured by accident", () =>
        {
            var parsed = PaladinUri.Parse("paladin://deep?game=253129753&url=https%3A%2F%2Fexample.com%2Fr.rec");
            True(parsed.Ok, parsed.Error ?? "");
            False(parsed.Force, "absent means no");
        });

        Test("an unknown value is ignored rather than refused, exactly as then= is", () =>
        {
            // An unrecognised flag is not a reason to throw away a good capture link.
            var parsed = PaladinUri.Parse("paladin://deep?game=1&url=https%3A%2F%2Fexample.com%2Fr.rec&force=yes-please");
            True(parsed.Ok, "the link still works");
            False(parsed.Force, "it simply does not force");
        });

        Test("ONLY A DEEP LINK MAY FORCE; a dump link carrying it does not", () =>
        {
            // force= means "re-capture a game Paladin already holds". A dump link asking for it would
            // be asking for something this flag does not mean.
            var parsed = PaladinUri.Parse("paladin://dump?game=1&url=https%3A%2F%2Fexample.com%2Fr.rec&force=1");
            True(parsed.Ok, parsed.Error ?? "");
            True(parsed.IsDump, "still a dump link");
            False(parsed.Force, "and it does not force");
        });

        Suite("Deep replay retention");

        Test("it posts to the replay route on the configured ORIGIN, never under /api/world/", () =>
        {
            // 871's bug, which got as far as launching a game: DumpUploadBaseUrl already ends in
            // /api/world/, so anything appended to the ADDRESS rather than its origin lands on the SPA.
            var uploader = new ReplayUploader("https://paladin.odinmaycall.com/api/world/", "0.5.2", new Core.Logging.PaladinLog(false));
            Equal("https://paladin.odinmaycall.com/api/replay/253132683", uploader.TargetFor(253132683).ToString());
            Equal("paladin.odinmaycall.com", uploader.Host);
        });

        Test("a test deploy still gets its own replays, so production is never hit by accident", () =>
        {
            var uploader = new ReplayUploader("https://test.paladin.pages.dev/api/world/", "0.5.2", new Core.Logging.PaladinLog(false));
            Equal("https://test.paladin.pages.dev/api/replay/12345", uploader.TargetFor(12345).ToString());
        });

        Test("COMPRESSION IS REAL AND ROUND-TRIPS, because the stored bytes are these bytes", () =>
        {
            // Paladin retains the gzip member verbatim, in the same .rec.gz format its own bank uses,
            // so what this produces is what is kept and later re-parsed.
            var replay = new byte[200_000];
            for (var i = 0; i < replay.Length; i++) replay[i] = (byte)(i % 251);
            var gz = ReplayUploader.Compress(replay);

            True(gz.Length > 2 && gz[0] == 0x1f && gz[1] == 0x8b, "it is a gzip member, which is what the Worker sniffs for");
            True(gz.Length < replay.Length, $"and it actually compresses: {gz.Length} from {replay.Length}");

            using var input = new MemoryStream(gz);
            using var gunzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gunzip.CopyTo(output);
            var back = output.ToArray();
            Equal(replay.Length, back.Length);
            True(back.AsSpan().SequenceEqual(replay), "byte for byte, or a re-parse would read different bytes than were played");
        });

        Test("THE WIRE CAP MATCHES THE WORKER'S, so a doomed body is never put on the wire", () =>
        {
            // Measured over 3,408 banked replays: worst compressed 1,579,711 bytes. The cap is 4 MiB.
            Equal(4L * 1024 * 1024, ReplayUploader.WireCapBytes);
            True(ReplayUploader.WireCapBytes > 1_579_711, "2.6x the largest replay ever banked");
        });

        Test("the reader is told what it is FOR, not what it is", () =>
        {
            var retaining = DeepConsoleText.RetainingReplay();
            True(retaining.Contains("replay", StringComparison.OrdinalIgnoreCase), retaining);
            False(retaining.Contains("gzip", StringComparison.OrdinalIgnoreCase), "no wire detail in a reader's line");

            var done = DeepConsoleText.ReplayRetained(295_000, 1_318_624, true);
            True(done.Contains("complete", StringComparison.OrdinalIgnoreCase), done);

            var pending = DeepConsoleText.ReplayRetained(295_000, 1_318_624, false);
            False(pending.Contains("complete", StringComparison.OrdinalIgnoreCase), "an unparsed one must not claim completeness");

            var failed = DeepConsoleText.ReplayNotRetained("could not reach paladin.odinmaycall.com");
            True(failed.Contains("capture is safe", StringComparison.OrdinalIgnoreCase), failed);
            True(failed.Contains("partial", StringComparison.OrdinalIgnoreCase), "and names the state the game will show");
        });
    }
}
