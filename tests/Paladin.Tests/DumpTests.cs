using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Paladin.Core.Config;
using Paladin.Core.Dump;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// "Dump this game" (§717), pass A: everything that runs without a game. The fixtures
/// are small excerpts of the owner's own dump-kit evidence — the lines the game printed
/// for two banked replays — with every personal line (USER, COMPUTER, WORKING-DIR, the
/// Steam name, the user's folder, the login token) replaced by a placeholder in its real
/// position, so that its exclusion is proven and not just by construction. No log file is
/// copied whole.
/// </summary>
public static class DumpTests
{
    // ---- fixtures ---------------------------------------------------------------------

    // SHA-256 of the kit's four console-paste files, measured with sha256sum on 18 Sept 2026.
    // The ladders in DumpLadder must reproduce those files byte for byte (LF endings, one
    // trailing newline), so these hashes are the byte-equality test without the kit folder.
    private const string KitHelloSha256 = "f8faa26a759612f930f859038442b680e36558150c08cd5223736ee5c10fa808";
    private const string KitEntitiesSha256 = "f3410affff005d0704e699592976433632c97d0f5428cbb13f5b27c5a8a1300f";
    private const string KitSquadsSha256 = "cc98d09279ccae016018d02666482b2f752970972dba4ee4056ad75e9f952634";
    private const string KitLenSha256 = "c3ea316d54edb9c8714fa4a8590374bc3e6cbd3a92fd1157edce5fa2b663c31a";

    /// <summary>
    /// The first 12 lines of the top-level warnings.log for the 249960029 run
    /// (K\out\AgeIV_Replay_249960029_toplevel_warnings.2026-09-18.04-53-19.txt), with the
    /// WORKING-DIR, USER and COMPUTER values replaced by placeholders.
    /// </summary>
    private static readonly string TopLevelHead = string.Join("\n", new[]
    {
        "RelicCardinal started at 2026-09-18 04:53 [GMT Summer Time UTC 00:00]",
        "OS Win 10.0.26200.0, 31895MB Physical Memory, 8096 Physical Available, 51211 Virtual Total, 9620 Virtual Available, 19315 Page file.",
        "RUN-OPTIONS [-dev -replay playback:AgeIV_Replay_249960029]",
        @"WORKING-DIR [D:\SteamLibrary\steamapps\common\Age of Empires IV]",
        "USER [someone]",
        "COMPUTER [DESKTOP-TEST]",
        "LOCALE [en-GB]",
        "",
        @"(I) [04:53:19.236] [000030660]: Version.cpp - translation info queried modulefilename D:\SteamLibrary\steamapps\common\Age of Empires IV\RelicCardinal.exe, len 1564",
        "(I) [04:53:19.236] [000030660]: Version [16.3.11308.0] Info [[Cardinal][cardinal][release_16_3_0][rtm][4178464]]",
        "(I) [04:53:20.607] [000030660]: Loading step: [Additional crash information]",
        "(I) [04:53:20.607] [000030660]: Loading step: [Platform]",
    });

    /// <summary>
    /// The same file to line 28: lines 13-27 verbatim but for the Steam name at 17 (a
    /// placeholder; it sits at line 17 in every one of the kit's 158 top-level copies), and
    /// line 28, the "Using [C:\Users\...]" writable-folder line, with a placeholder path.
    /// Everything past line 12 must be past the cut.
    /// </summary>
    private static readonly string TopLevelHeadLong = TopLevelHead + "\n" + string.Join("\n", new[]
    {
        "(I) [04:53:20.609] [000030660]: Loading step: [Early Free RAM Check]",
        "(I) [04:53:20.609] [000030660]: Loading step: [Single Instance Check]",
        "(I) [04:53:20.609] [000030660]: Loading step: [Async IO Queue]",
        "(I) [04:53:20.609] [000030660]: Loading step: [Config File]",
        "(I) [04:53:20.717] [000030660]: GAME -- Current Steam name is [someone]",
        "(I) [04:53:20.717] [000030660]: GAME -- steam returned language 'english'",
        "(I) [04:53:20.717] [000030660]: GAME -- [Age of Empires IV] set to language [en]",
        "(I) [04:53:20.717] [000030660]: Loading step: [Render Window]",
        "(I) [04:53:20.717] [000030660]: Loading step: [Statgraph]",
        "(I) [04:53:20.717] [000030660]: Loading step: [StatGraph Stats]",
        "(I) [04:53:20.717] [000030660]: Loading step: [MathBox]",
        "(I) [04:53:20.717] [000030660]: MATHBOX -- Mode=SSE",
        "(I) [04:53:20.717] [000030660]: Loading step: [ArchiveManager]",
        "(I) [04:53:20.717] [000030660]: Loading step: [IOThrottler]",
        "(I) [04:53:20.717] [000030660]: Loading step: [Filesystem]",
        @"(I) [04:53:20.718] [000030660]: Using [C:\Users\someone\OneDrive\Private Folder\My Games\Age of Empires IV\] as base writable folder",
    });

    private const string VersionLine = "(I) [04:53:19.236] [000030660]: Version [16.3.11308.0] Info [[Cardinal][cardinal][release_16_3_0][rtm][4178464]]";

    /// <summary>
    /// Lines of the 249960029 session file (K\out\AgeIV_Replay_249960029_warnings.2026-09-18.04-53-19.txt:
    /// 1-3, 14, 589-590, 676-681, 817, 1056, 1062, 1072-1073, 1077-1083) and a DONE line adapted to
    /// the three rows kept here (the real one, line 1281, is PALADIN2_DONE|0|199|200). The
    /// Steam name (3), the user's folder (14) and the login token (589) are placeholders in
    /// their real positions.
    /// </summary>
    private static readonly string[] SessionLines =
    {
        "-- Log file for all dbTracef messages --",
        "",
        "04:53:20.717   GAME -- Current Steam name is [someone]",
        @"04:53:20.718   Using [C:\Users\someone\OneDrive\Private Folder\My Games\Age of Empires IV\] as base writable folder",
        "04:53:30.594   Login attempt: PLACEHOLDERTOKEN 000000000000",
        @"04:53:30.616   UI Async loading data:ui\shared\modalpages\UserPromptModalPage.xaml",
        "04:53:33.633   MapGen -- Generating with biome taiga_woodlands_snow",
        "04:53:33.633   MapGen -- Generating with layout highview",
        "04:53:33.633   MapGen -- Generating with size map_size_416",
        "04:53:33.633   MapGen -- Generating with seed 1176837288 (0x46251CA8)",
        "04:53:33.633   MapGen -- Generating with player count 2",
        "04:53:33.633   MapGen -- Generating with players locations: Fixed (Teams Together)",
        "04:53:36.639   'ALT+F4' has been bound to 'quit()'",
        "04:53:38.737   GAME -- Starting mission: ",
        @"04:53:38.793   UI Async loading data:ui\shared\modalpages\UserPromptModalPage.xaml",
        "04:53:44.792   PALADIN3_HELLO|function: 00007FF712F17B30|function: 0000025FA6270070",
        "04:53:44.792   PALADIN3_ENV|2021|6.0|true",
        "04:54:07.791   PALADIN2_DEF|function: 000002603B4F8A20|function: 0000026008FDE120|function: 000002601A834BB0|function: 000002603BB20690|function: 000002600862CCA0|function: 000002600862F1C0|function: 000002600862D900",
        "04:54:10.918   PALADIN2_BEGIN|2026|32.125",
        "04:54:10.919   PALADIN2_PLAYER|1|May|sultanate|1|32.5|128.5",
        "04:54:10.919   PALADIN2_PLAYER|2|??Creator|ottoman|0|-31.5|-127.5",
        "04:54:13.919   PALADIN2|0|1000005002|-1|spruce_tall_wild|-79.5|17.162261962891|-114.5|world",
        "04:54:13.919   PALADIN2|1|1000005003|-1|spruce_med_wild|-75.5|17.769708633423|-110.5|world",
        "04:54:13.919   PALADIN2|2|1000005004|-1|spruce_tall_wild|-83.5|15.747743606567|-110.5|world",
        "04:54:13.920   PALADIN2_DONE|0|2|3",
    };

    private static readonly string Session = string.Join("\n", SessionLines);

    /// <summary>The first session line carrying <paramref name="text"/>, by content rather than by index.</summary>
    private static string Line(string text) => SessionLines.First(l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>The same excerpt with the ENV count set to the rows kept, so the completeness rule is met.</summary>
    private static readonly string SessionComplete = Session.Replace("PALADIN3_ENV|2021|", "PALADIN3_ENV|3|");

    /// <summary>
    /// The 244129849 fatal (K\out\AgeIV_Replay_244129849_warnings.2026-09-17.22-48-31.txt:1044-1047),
    /// the padding x's shortened. The kit's substring matcher read line 1045 as proof that
    /// "PALADIN2_LEN|320" had landed; it is the line that killed the game.
    /// </summary>
    private const string FatalEnvLine = "22:48:57.411   PALADIN3_ENV|1685|5.875|true";
    private const string FatalLine =
        """22:49:00.537   GameObj::OnFatalScarError: [[string "__Internal_Game_Quicksaveprint("PALADIN2_LEN|320") --xxxxxxxxxxxxxxxx..."]:1: attempt to call a nil value (global '__Internal_Game_Quicksaveprint')]""";
    private const string FatalLine2 =
        """22:49:01.928   *FATAL SCAR ERROR: [string "__Internal_Game_Quicksaveprint("PALADIN2_LEN|320") --xxxxxxxxxxxxxxxx..."]:1: attempt to call a nil value (global '__Internal_Game_Quicksaveprint')""";
    private const string FatalTopLevel =
        """(I) [22:49:00.537] [000012912]: GameObj::OnFatalScarError: [[string "__Internal_Game_Quicksaveprint("PALADIN2_LEN|320") --xxxxxxxxxxxxxxxx..."]:1: attempt to call a nil value (global '__Internal_Game_Quicksaveprint')]""";

    // The banked 246737201 rows in the top-level log's own form (K\out\AgeIV_Replay_246737201_warnings.log:2167).
    private const string TopLevelRow = "(I) [21:05:57.418] [000010736]: PALADIN2|0|1000005002|-1|holy_site_swamp|-126.0|4.8464202880859|-126.0|world";

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static PaladinLog Quiet() => new(echoToConsole: false);

    // ---- an in-process HTTP stub -------------------------------------------------------

    private sealed record Sent(string Method, string Url, string? UserAgent, string? Launcher, string? Accept, string? ContentType, string? Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
        public List<Sent> Requests { get; } = new();

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public StubHandler(HttpStatusCode code, string body)
            : this((_, _) => Task.FromResult(Json(code, body))) { }

        public static HttpResponseMessage Json(HttpStatusCode code, string body) =>
            new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json"), ReasonPhrase = code.ToString() };

        /// <summary>A 200 whose headers arrive and whose body read throws IOException: the line cut under the answer.</summary>
        public static HttpResponseMessage Cut()
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CutStream()), ReasonPhrase = "OK" };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return response;
        }

        /// <summary>Each call takes the next answer; the last one repeats. A throwing lambda is a network failure.</summary>
        public static StubHandler Sequence(params Func<HttpResponseMessage>[] answers)
        {
            var n = 0;
            return new StubHandler((_, _) => Task.FromResult(answers[Math.Min(n++, answers.Length - 1)]()));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : null,
                request.Headers.TryGetValues(DumpUploader.LauncherHeader, out var lv) ? string.Join(" ", lv) : null,
                request.Headers.TryGetValues("Accept", out var acc) ? string.Join(" ", acc) : null,
                request.Content?.Headers.ContentType?.ToString(),
                body));
            return await _answer(request, ct);
        }
    }

    /// <summary>A readable stream whose every read is a reset connection.</summary>
    private sealed class CutStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("The connection was reset under the body.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("The connection was reset under the body.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The back-off is zero unless a test says otherwise, so the retry tests do not wait 5 s.</summary>
    private static DumpUploader Uploader(StubHandler handler, TimeSpan? uploadTimeout = null, TimeSpan? preflightTimeout = null, TimeSpan? retryDelay = null) =>
        new("https://paladin.example/api/world/", "0.4.0-test", Quiet(), new HttpClient(handler), uploadTimeout, preflightTimeout, retryDelay ?? TimeSpan.Zero);

    private static DumpEnvelope SmallEnvelope(long gameId = 246737201) =>
        DumpEnvelope.From(
            DumpEvidence.Build(TopLevelHead, SessionComplete), gameId, "0.4.0-test", "20260918-045319-abc123",
            new DateTime(2026, 9, 18, 3, 54, 13, DateTimeKind.Utc), 11308, "altshift", "paste",
            new DumpTimings { MissionStartS = 19.5, HelloS = 25.8, DumpS = 54.9 });

    public static void Register()
    {
        Suite("DumpLadder (the console lines)");

        Test("the HELLO line is the kit's, byte for byte, 250 characters, 256 guarded", () =>
        {
            Equal(250, DumpLadder.Hello.Length);
            Equal(KitHelloSha256, Sha256(DumpLadder.Hello + "\n"), "console-paste-hello.txt");
            Equal(DumpLadder.GuardedHelloLength, DumpLadder.Guard(DumpLadder.Hello).Length);
            True(DumpLadder.Guard(DumpLadder.Hello).StartsWith("_=nil print(\"PALADIN3_HELLO|\"", StringComparison.Ordinal), "guarded");
        });

        Test("the entity ladder is the kit's 11 lines, byte for byte, with the measured lengths", () =>
        {
            var lengths = new[] { 103, 79, 120, 87, 167, 153, 136, 104, 189, 116, 153 };
            Equal(11, DumpLadder.EntityLadder.Count);
            for (var i = 0; i < lengths.Length; i++)
                Equal(lengths[i], DumpLadder.EntityLadder[i].Length, $"line {i + 1}");
            Equal(KitEntitiesSha256, Sha256(string.Join("\n", DumpLadder.EntityLadder) + "\n"), "console-paste-entities-chunk.txt");
            True(DumpLadder.EntityLadder[^1].StartsWith("print(\"PALADIN2_DEF|\"", StringComparison.Ordinal), "the sentinel is last");
        });

        Test("the squad ladder is the kit's 9 lines, byte for byte", () =>
        {
            var lengths = new[] { 118, 87, 129, 113, 167, 100, 103, 142, 154 };
            Equal(9, DumpLadder.SquadLadder.Count);
            for (var i = 0; i < lengths.Length; i++)
                Equal(lengths[i], DumpLadder.SquadLadder[i].Length, $"line {i + 1}");
            Equal(KitSquadsSha256, Sha256(string.Join("\n", DumpLadder.SquadLadder) + "\n"), "console-paste-squads.txt");
            True(DumpLadder.SquadLadder[^1].StartsWith("print(\"PALADIN3_SQDEF|\"", StringComparison.Ordinal), "the sentinel is last");
        });

        Test("every ladder line is under the kit's 190 cap before the guard and 195 after it; all far under the proven 768", () =>
        {
            foreach (var line in DumpLadder.EntityLadder.Concat(DumpLadder.SquadLadder))
            {
                True(line.Length <= DumpLadder.MaxLadderLineLength, $"{line.Length} chars: {line[..20]}");
                var guarded = DumpLadder.Guard(line);
                True(guarded.Length <= DumpLadder.MaxGuardedLadderLineLength, $"{guarded.Length} chars guarded: {line[..20]}");
                True(guarded.Length <= DumpLadder.ProvenConsoleLineCap, "under the proven cap");
            }
            foreach (var single in new[] { DumpLadder.Begin, DumpLadder.SquadCall, DumpLadder.Quit, DumpLadder.Chunk(3800, 3999) })
                True(DumpLadder.Guard(single).Length <= DumpLadder.MaxGuardedLadderLineLength, single);
            Equal(195, DumpLadder.EntityLadder.Concat(DumpLadder.SquadLadder).Max(l => DumpLadder.Guard(l).Length), "the longest guarded ladder line");
            True(DumpLadder.GuardedHelloLength <= DumpLadder.ProvenConsoleLineCap, "HELLO under the proven cap too");
        });

        Test("the guard is exactly '_=nil ' and every line gets it", () =>
        {
            Equal("_=nil ", DumpLadder.GuardPrefix);
            Equal("_=nil quit()", DumpLadder.Guard(DumpLadder.Quit));
            Equal("_=nil PD2(0,199)", DumpLadder.Guard(DumpLadder.Chunk(0, 199)));
            Equal("_=nil " + DumpLadder.Begin, DumpLadder.Guard(DumpLadder.Begin));
        });

        Test("BEGIN and the squad call are the kit's exact lines", () =>
        {
            Equal("print(\"PALADIN2_BEGIN|\"..World_GetNumEntities()..\"|\"..World_GetGameTime()) PL()", DumpLadder.Begin, "launch-and-dump.ps1:240");
            Equal("if not pcall(SQ) then print(\"PALADIN3_SQERR|begin\") end", DumpLadder.SquadCall, "launch-and-dump.ps1:253");
            Equal("PALADIN2_DONE|0|199|", DumpLadder.DoneMarker(0, 199));
        });

        Test("chunks of 200 cover 0..n-1 with the last one cut to n-1", () =>
        {
            var c1465 = DumpLadder.Chunks(1465);
            Equal(8, c1465.Count, "1,465 entities: 8 chunks (the West Lake run)");
            Equal((0, 199), c1465[0]);
            Equal((1400, 1464), c1465[^1]);

            var c2021 = DumpLadder.Chunks(2021);
            Equal(11, c2021.Count, "2,021 entities: 11 chunks (the High View run)");
            Equal((2000, 2020), c2021[^1], "DONE|2000|2020|21 is what the log shows");

            Equal(1, DumpLadder.Chunks(200).Count);
            Equal((0, 199), DumpLadder.Chunks(200)[0]);
            Equal(2, DumpLadder.Chunks(201).Count);
            Equal((200, 200), DumpLadder.Chunks(201)[1]);
            Equal(0, DumpLadder.Chunks(0).Count, "nothing to print");
            Equal(4, DumpLadder.Chunks(1000, chunkSize: 250).Count, "the chunk size is a knob");
        });

        Test("the line-cap probes are exactly 320/512/768 characters as delivered, and rebuild the kit's file", () =>
        {
            foreach (var n in DumpLadder.ProbeLengths)
            {
                Equal(n, DumpLadder.Guard(DumpLadder.Probe(n)).Length, $"probe {n} guarded");
                True(DumpLadder.Probe(n).StartsWith($"print(\"PALADIN2_LEN|{n}\") --", StringComparison.Ordinal), "prints its own length");
            }
            // The kit's lines were the same text with six more x's, i.e. 320/512/768 before a guard existed.
            var kitLen = string.Join("\n", DumpLadder.ProbeLengths.Select(n => DumpLadder.Probe(n) + new string('x', DumpLadder.GuardPrefix.Length))) + "\n";
            Equal(KitLenSha256, Sha256(kitLen), "console-paste-len.txt");
            Equal(768, DumpLadder.ProbeLengths[^1]);
            Equal(DumpLadder.ProvenConsoleLineCap, DumpLadder.ProbeLengths[^1], "the largest probe is the proven cap");
            Throws<ArgumentOutOfRangeException>(() => DumpLadder.Probe(10), "shorter than its own text");
        });

        Test("the script after HELLO is the ladder, BEGIN, one PD2 a chunk, optionally the squads, then quit(), every line guarded", () =>
        {
            var plain = DumpLadder.Script(1465, includeSquads: false);
            Equal(11 + 1 + 8 + 1, plain.Count);
            True(plain.All(l => l.Text.StartsWith(DumpLadder.GuardPrefix, StringComparison.Ordinal)), "all guarded");
            True(plain.All(l => l.Text.Length <= DumpLadder.ProvenConsoleLineCap), "all under the cap");
            Equal(null, plain[0].Marker, "a ladder line before the sentinel prints nothing");
            Equal(DumpLadder.DefMarker, plain[10].Marker, "the sentinel proves the ladder");
            Equal(DumpLadder.BeginMarker, plain[11].Marker);
            Equal("PALADIN2_DONE|0|199|", plain[12].Marker);
            Equal("PALADIN2_DONE|1400|1464|", plain[19].Marker);
            Equal("_=nil quit()", plain[^1].Text);
            Equal(null, plain[^1].Marker, "quit() is sent, never waited for");

            var withSquads = DumpLadder.Script(1465, includeSquads: true);
            Equal(plain.Count + 9 + 1, withSquads.Count);
            // 11 ladder + BEGIN + 8 chunks = 20 lines, then the 9 squad lines (indices 20-28), the call (29), quit (30).
            Equal(null, withSquads[20].Marker, "the first squad line prints nothing");
            Equal(DumpLadder.SquadDefMarker, withSquads[28].Marker, "the squad sentinel");
            Equal(DumpLadder.SquadBeginMarker, withSquads[29].Marker, "the call");
            Equal("_=nil quit()", withSquads[^1].Text);
        });

        // ---------------------------------------------------------------------------

        Suite("DumpLogText (reading the game's logs)");

        Test("takes the prefix off both log forms and refuses the game's unprefixed lines", () =>
        {
            True(DumpLogText.TryParse("04:53:44.792   PALADIN3_HELLO|x", out var s), "session form");
            Equal(LogLineForm.Session, s.Form);
            Equal("04:53:44.792", s.Clock);
            Equal("PALADIN3_HELLO|x", s.Message);

            True(DumpLogText.TryParse(VersionLine, out var t), "top-level form");
            Equal(LogLineForm.TopLevel, t.Form);
            Equal("04:53:19.236", t.Clock);
            True(t.Message.StartsWith("Version [16.3.11308.0]", StringComparison.Ordinal), "message after the thread id");

            True(DumpLogText.TryParse("(E) [04:53:19.236] [000030660]: something", out _), "the (E) level letter too");
            False(DumpLogText.TryParse("-- Log file for all dbTracef messages --", out _), "the banner");
            False(DumpLogText.TryParse("Type checksum mismatch for executable type checksum 1 against baked object checksum 2 for TypeID 3", out _), "an unprefixed warning");
            False(DumpLogText.TryParse("", out _), "empty");
            True(DumpLogText.TryParse("\uFEFF21:05:30.171   PALADIN3_HELLO|x", out _), "a BOM on the first line is tolerated");
            True(DumpLogText.TryParse("21:05:30.171   PALADIN3_HELLO|x\r", out _), "a CR is tolerated");
        });

        Test("a marker counts only at the anchor: the 244129849 fatal line is not a PALADIN2_LEN line", () =>
        {
            True(FatalLine.Contains("PALADIN2_LEN|320", StringComparison.Ordinal), "the substring IS there — that is the trap the kit fell into");
            Equal(null, DumpLogText.Marker(FatalLine), "not a marker");
            False(DumpLogText.HasMarker(FatalLine, "PALADIN2_LEN|320"), "not that marker either");
            True(DumpLogText.IsFatal(FatalLine), "it is a fatal");
            True(DumpLogText.IsFatal(FatalLine2), "*FATAL SCAR ERROR too");
            True(DumpLogText.IsFatal(FatalTopLevel), "and in the top-level form");
            Equal(null, DumpLogText.Marker(FatalTopLevel));

            Equal("PALADIN3_HELLO", DumpLogText.Marker("04:53:44.792   PALADIN3_HELLO|function: 00007FF712F17B30|function: 0000025FA6270070"), "the design's example");
            Equal("PALADIN2", DumpLogText.Marker(TopLevelRow), "top-level row form");
            Equal("PALADIN2_DONE", DumpLogText.Marker("04:54:44.416   PALADIN2_DONE|2000|2020|21"));
            Equal("PALADIN3_SQ_DONE", DumpLogText.Marker("04:55:01.544   PALADIN3_SQ_DONE|93"));
            Equal("PALADIN3_SQ", DumpLogText.Marker("04:55:01.543   PALADIN3_SQ|1|50000|gaia_huntable_deer|20.4|12.6|-174.0|1|1000005142|world"));
            Equal(null, DumpLogText.Marker("04:53:44.792   note PALADIN3_HELLO|x"), "mid-line is not anchored");
            Equal(null, DumpLogText.Marker("PALADIN3_HELLO|x"), "no clock, no marker");
            Equal(null, DumpLogText.Marker("04:53:44.792   PALADIN3_HELLO"), "no pipe, no marker");
        });

        Test("mission start, the account prompt and the not-signed-in fatal are detected", () =>
        {
            True(DumpLogText.IsMissionStart("04:53:38.737   GAME -- Starting mission: "), "session line 1056");
            True(DumpLogText.IsMissionStart("(I) [21:05:23.967] [000010736]: GAME -- Starting mission: "), "top-level 1229");
            False(DumpLogText.IsMissionStart("04:53:36.639   'ALT+F4' has been bound to 'quit()'"), "another line");
            True(DumpLogText.IsAccountPrompt(@"04:53:30.616   UI Async loading data:ui\shared\modalpages\UserPromptModalPage.xaml"), "session line 590");
            False(DumpLogText.IsAccountPrompt("04:53:36.639   'ALT+F4' has been bound to 'quit()'"), "a different line");
            True(DumpLogText.IsScriptsUnavailable("""22:00:00.000   GameObj::OnFatalScarError: [[string "_=nil quit()"]:1: attempt to call a nil value (global 'quit')]"""), "F3");
            False(DumpLogText.IsScriptsUnavailable(FatalLine), "a different nil global is not the sign-in case");
        });

        Test("ENV gives the entity count, the game time and dev mode; a non-numeric count is kept as text (F3)", () =>
        {
            True(DumpLogText.TryEnv("04:53:44.792   PALADIN3_ENV|2021|6.0|true", out var env), "the High View ENV");
            Equal(2021, env.Count!.Value);
            Equal(6.0, env.GameTime!.Value);
            Equal(true, env.DevMode);

            True(DumpLogText.TryEnv("21:05:30.171   PALADIN3_ENV|1465|6.125|true", out var west), "the West Lake ENV");
            Equal(1465, west.Count!.Value);
            Equal(6.125, west.GameTime!.Value);

            True(DumpLogText.TryEnv("21:05:30.171   PALADIN3_ENV|nil|nil|nil", out var nil), "still an ENV line");
            Equal(null, nil.Count);
            Equal("nil", nil.RawCount);
            Equal(null, nil.DevMode);

            False(DumpLogText.TryEnv("04:53:44.792   PALADIN3_HELLO|x|y", out _), "not ENV");
        });

        Test("DONE gives the chunk's range and count", () =>
        {
            True(DumpLogText.TryDone("04:54:13.920   PALADIN2_DONE|0|199|200", out var first), "the first chunk");
            Equal(new DoneLine(0, 199, 200), first);
            True(DumpLogText.TryDone("04:54:44.416   PALADIN2_DONE|2000|2020|21", out var last), "the last chunk");
            Equal(new DoneLine(2000, 2020, 21), last);
            False(DumpLogText.TryDone("04:54:13.920   PALADIN2_DONE|0|199", out _), "three fields or nothing");
        });

        Test("DEF is the ladder's sentinel and a nil in it is a dropped line (F4)", () =>
        {
            True(DumpLogText.TryDef(Line("PALADIN2_DEF|"), out var nil1), "the real DEF line");
            False(nil1, "seven functions, no nil");
            True(DumpLogText.TryDef("04:54:07.791   PALADIN2_DEF|function: 0000|nil|function: 0000|function: 0000|function: 0000|function: 0000|function: 0000", out var nil2), "a DEF with a nil");
            True(nil2, "PP was dropped");
            False(DumpLogText.TryDef("04:54:07.791   PALADIN3_SQDEF|function: 0000", out _), "the squad sentinel is its own");
            True(DumpLogText.TrySquadDef("04:54:58.667   PALADIN3_SQDEF|function: 0000|function: 0000", out var sqNil), "the squad sentinel");
            False(sqNil, "no nil");
        });

        Test("PLAYER rows read as parsePlayerRows does, name from the end, ANSI-lost characters kept as printed", () =>
        {
            True(DumpLogText.TryPlayer("04:54:10.919   PALADIN2_PLAYER|1|May|sultanate|1|32.5|128.5", out var p1), "player 1");
            Equal(new PlayerLine(1, "May", "sultanate", 1, 32.5, 128.5), p1);
            True(DumpLogText.TryPlayer("04:54:10.919   PALADIN2_PLAYER|2|??Creator|ottoman|0|-31.5|-127.5", out var p2), "player 2");
            Equal("??Creator", p2.Name, "Loc_ToAnsi's '?' for a non-Latin name, session 249960029 line 1080");
            Equal("ottoman", p2.Civ);
            Equal(-31.5, p2.X!.Value);
            True(DumpLogText.TryPlayer("21:05:54.294   PALADIN2_PLAYER|1|a|b|french|1|128.5|-127.5", out var piped), "a piped name");
            Equal("a|b", piped.Name, "a '|' in the name survives");
            True(DumpLogText.TryPlayer("21:05:54.294   PALADIN2_PLAYER|1|x|french|1||", out var noStart), "no start position");
            Equal(null, noStart.X, "Player_GetStartingPosition failed: empty fields");
            False(DumpLogText.TryPlayer("21:05:54.294   PALADIN2_PLAYERERR|1", out _), "PLAYERERR is not a player row");
        });

        Test("entity rows read as parseDumpLog does: index, id, squad (-1 = none), blueprint, x y z, owner", () =>
        {
            True(DumpLogText.TryEntity(Line("PALADIN2|0|"), out var e0), "the first row");
            Equal(0, e0.Index);
            Equal(1000005002L, e0.Id);
            Equal(null, e0.Squad, "-1 is no squad");
            Equal("spruce_tall_wild", e0.Blueprint);
            Equal(-79.5, e0.X);
            Equal(17.162261962891, e0.Y);
            Equal(-114.5, e0.Z);
            Equal("world", e0.Owner);

            True(DumpLogText.TryEntity("17:17:23.455   PALADIN2|2038|1000007040|50090|gaia_herdable_sheep|-38.97|25.54|-135.31|2", out var sheep), "a sheep in a squad");
            Equal(50090, sheep.Squad!.Value);
            Equal("2", sheep.Owner, "player 2's");
            True(DumpLogText.TryEntity("17:17:23.455   PALADIN2|3|1000005005|-1|x|1|2|3|?", out var unknown), "an unknown owner");
            Equal(null, unknown.Owner, "'?' is unknown");
            True(DumpLogText.TryEntity(TopLevelRow, out var top), "top-level form");
            Equal("holy_site_swamp", top.Blueprint);
            True(DumpLogText.TryEntity("17:17:23.455   PALADIN2|4|1000005006|-1|x|1e-05|2|3|world", out var tiny), "Lua's %.14g exponent form");
            Equal(1e-05, tiny.X);
            False(DumpLogText.TryEntity("04:54:13.920   PALADIN2_DONE|0|2|3", out _), "DONE is not a row");
        });

        Test("the squad markers and the MapGen lines parse", () =>
        {
            True(DumpLogText.TrySquadCount("04:55:01.543   PALADIN3_SQ_BEGIN|93", out var isDone, out var n), "SQ_BEGIN");
            False(isDone, "BEGIN is not DONE");
            Equal(93, n);
            True(DumpLogText.TrySquadCount("04:55:01.544   PALADIN3_SQ_DONE|93", out isDone, out n), "SQ_DONE");
            True(isDone, "DONE");
            True(DumpLogText.TryMapGen("04:53:33.633   MapGen -- Generating with seed 1176837288 (0x46251CA8)", out var key, out var value), "the seed line");
            Equal("seed", key);
            Equal("1176837288 (0x46251CA8)", value);
            True(DumpLogText.TryMapGen("04:53:33.633   MapGen -- Generating with player count 2", out key, out value), "the player count line");
            Equal("player count", key);
            Equal("2", value);
            False(DumpLogText.TryMapGen("04:53:33.633   MapGen -- Generating with players locations: Fixed (Teams Together)", out _, out _), "the sixth line is not kept");
        });

        Test("a whole session summarises: markers, counts, detections, and the completeness rule", () =>
        {
            var s = DumpLogText.Summarise(Session);
            True(s.HelloSeen, "HELLO");
            Equal(2021, s.EnvCount);
            True(s.DefSeen && !s.DefHasNil, "DEF clean");
            True(s.BeginSeen, "BEGIN");
            Equal(3, s.EntityRows);
            Equal(2, s.PlayerRows);
            Equal(0, s.ErrRows);
            Equal(1, s.Chunks.Count);
            Equal(new DoneLine(0, 2, 3), s.Chunks[0]);
            True(s.MissionStarted, "mission");
            Equal(2, s.AccountPrompts, "the prompt at +11 s and again after the mission starts");
            False(s.Fatal, "no fatal");
            Equal("04:54:13.919", s.FirstRowClock);
            Equal("04:54:13.920", s.LastDoneClock);
            False(s.Complete, "3 of 2,021 printed: incomplete (F5)");

            True(DumpLogText.Summarise(SessionComplete).Complete, "rows >= ENV: complete");

            var fatal = DumpLogText.Summarise(string.Join("\n", FatalEnvLine, FatalLine, "22:49:00.537   GAME -- SimulationController::Pause 1", FatalLine2));
            True(fatal.Fatal, "fatal seen");
            Equal(FatalLine, fatal.FatalLine, "the first fatal line is kept whole");
            Equal(1685, fatal.EnvCount, "ENV before it still counts");
            False(fatal.Complete, "a fatal run is never complete");
        });

        Test("Lines() splits CRLF text and drops a leading BOM", () =>
        {
            var lines = DumpLogText.Lines("\uFEFFa\r\nb\r\nc").ToList();
            Equal(3, lines.Count);
            Equal("a", lines[0]);
            Equal("c", lines[2]);
            Equal(0, DumpLogText.Lines(null).Count());
        });

        // ---------------------------------------------------------------------------

        Suite("DumpEvidence (what leaves the machine)");

        Test("keeps exactly 3 + 5 header lines, in the envelope's order, by their exact forms", () =>
        {
            var e = DumpEvidence.Build(TopLevelHead, Session);
            Equal(DumpEvidence.HeaderLineCount, e.HeaderLines.Count);
            True(e.HeaderComplete, "complete");
            Equal("RelicCardinal started at 2026-09-18 04:53", e.HeaderLines[0], "cut before the time zone");
            Equal("RUN-OPTIONS [-dev -replay playback:AgeIV_Replay_249960029]", e.HeaderLines[1]);
            Equal(VersionLine, e.HeaderLines[2], "verbatim with its prefix, as the worked example has it");
            Equal("04:53:33.633   MapGen -- Generating with biome taiga_woodlands_snow", e.HeaderLines[3]);
            Equal("04:53:33.633   MapGen -- Generating with layout highview", e.HeaderLines[4]);
            Equal("04:53:33.633   MapGen -- Generating with size map_size_416", e.HeaderLines[5]);
            Equal("04:53:33.633   MapGen -- Generating with seed 1176837288 (0x46251CA8)", e.HeaderLines[6]);
            Equal("04:53:33.633   MapGen -- Generating with player count 2", e.HeaderLines[7]);
            False(e.Header.Contains("players locations", StringComparison.Ordinal), "the sixth MapGen line is not kept");

            Equal("2026-09-18 04:53", e.StartedAt);
            Equal("-dev -replay playback:AgeIV_Replay_249960029", e.RunOptions);
            Equal("16.3.11308.0", e.GameVersion);
            Equal("taiga_woodlands_snow", e.Biome);
            Equal("highview", e.Layout);
            Equal("map_size_416", e.Size);
            Equal(1176837288L, e.Seed);
            Equal(2, e.PlayerCount);
            True(e.NamesReplay(249960029), "RUN-OPTIONS names this game");
            False(e.NamesReplay(246737201), "and not another");
        });

        Test("never carries USER, COMPUTER, WORKING-DIR, LOCALE, the OS line, the install path, the Steam name, a user path or the login token", () =>
        {
            // The inputs carry every one of them (placeholders in the real positions), so the
            // exclusion is proven on the output and not vacuous.
            var input = TopLevelHeadLong + "\n" + Session;
            foreach (var carried in new[] { "USER [someone]", "COMPUTER [DESKTOP-TEST]", "WORKING-DIR [", "modulefilename", "Current Steam name is [someone]", @"C:\Users\someone\OneDrive", "Login attempt: PLACEHOLDERTOKEN" })
                True(input.Contains(carried, StringComparison.Ordinal), $"the fixture carries '{carried}'");

            var e = DumpEvidence.Build(TopLevelHeadLong, Session);
            True(e.HeaderComplete, "the eight lines were still found");
            var all = e.Text;
            foreach (var forbidden in new[] { "USER [", "COMPUTER [", "WORKING-DIR [", "LOCALE [", "OS Win", "modulefilename", "someone", "DESKTOP-TEST", "SteamLibrary", "Loading step", "Login attempt", "PLACEHOLDERTOKEN", "UserPromptModalPage", "Steam name", @"C:\Users", "OneDrive", "Private Folder", "Using [" })
                False(all.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"'{forbidden}' must not leave the machine");
            Equal(null, DumpEvidence.PersonalLine(e.HeaderLines), "the guard agrees");
            Equal(null, DumpEvidence.PersonalLine(e.RowLines));
        });

        Test("the personal-line guard names each forbidden form, and Build refuses without repeating the line", () =>
        {
            Equal("USER [", DumpEvidence.PersonalForm("USER [someone]"));
            Equal("COMPUTER [", DumpEvidence.PersonalForm("COMPUTER [x]"));
            Equal("WORKING-DIR [", DumpEvidence.PersonalForm(@"WORKING-DIR [C:\x]"));
            // The locale never reaches the guard — the selectors take three exact forms and
            // this is not one of them — but the class comment, the README and the release
            // notes all say a LOCALE line would be refused, so it is refused.
            Equal("LOCALE [", DumpEvidence.PersonalForm("LOCALE [en-GB]"));
            Equal("modulefilename", DumpEvidence.PersonalForm("(I) [1] [2]: Version.cpp - translation info queried modulefilename x"));
            Equal("GAME -- Current Steam name is [", DumpEvidence.PersonalForm(Line("Current Steam name")), "the session form, after the clock");
            Equal("GAME -- Current Steam name is [", DumpEvidence.PersonalForm("(I) [04:53:20.717] [000030660]: GAME -- Current Steam name is [someone]"), "the top-level form");
            Equal(@":\Users\", DumpEvidence.PersonalForm(Line("as base writable folder")), "any Windows user path");
            Equal(null, DumpEvidence.PersonalForm("RelicCardinal started at 2026-09-18 04:53"));
            Equal(null, DumpEvidence.PersonalForm(VersionLine));
            Equal(null, DumpEvidence.PersonalForm("04:54:10.919   PALADIN2_PLAYER|1|May|sultanate|1|32.5|128.5"), "a player row");
            Equal("USER [someone]", DumpEvidence.PersonalLine(new[] { "RUN-OPTIONS [x]", "USER [someone]" }));
            Equal(null, DumpEvidence.PersonalLine(new[] { "RelicCardinal started at 2026-09-18 04:53", VersionLine }));

            // A row can only carry what the ladder prints, so this cannot happen; the guard is
            // there so it never can, and when it fires the message names the form, never the
            // line: the message reaches the console and dump.json's Failure field.
            var contrived = Session + "\n" + @"04:54:10.920   PALADIN2_PLAYER|3|C:\Users\someone|french|1|1|2";
            try
            {
                DumpEvidence.Build(TopLevelHead, contrived);
                throw new AssertionException("Build did not refuse");
            }
            catch (DumpEvidenceException ex)
            {
                True(ex.Message.Contains(@":\Users\", StringComparison.Ordinal), ex.Message);
                False(ex.Message.Contains("someone", StringComparison.OrdinalIgnoreCase), "the refused line's content is not repeated");
                False(ex.Message.Contains("PALADIN2_PLAYER", StringComparison.Ordinal), "nor the line itself");
            }
            Throws<DumpEvidenceException>(() => DumpEvidence.Build(TopLevelHead, Session + "\n04:54:13.921   PALADIN2|3|1000005005|-1|modulefilename|1|2|3|world"), "the install path's word");
        });

        Test("only the first 12 lines of the top-level log are looked at", () =>
        {
            var late = string.Join("\n", Enumerable.Repeat("(I) [04:53:20.607] [000030660]: Loading step: [x]", 12)) + "\n" + VersionLine;
            var e = DumpEvidence.Build(late, Session);
            Equal(5, e.HeaderLines.Count, "no top-level line qualified");
            Equal(null, e.GameVersion);
            False(e.HeaderComplete, "seven lines is not a complete header");

            var lines = DumpEvidence.TopLevelHeaderLines(TopLevelHead);
            Equal(3, lines.Count);
            var repeated = TopLevelHead + "\n" + TopLevelHead;
            Equal(3, DumpEvidence.TopLevelHeaderLines(repeated).Count, "each form once");

            // Lines 13-28 of the real file, the Steam name at 17 and the user folder at 28 among them, are past the cut.
            var longer = DumpEvidence.TopLevelHeaderLines(TopLevelHeadLong);
            Equal(3, longer.Count, "lines 13-28 add nothing");
            for (var i = 0; i < 3; i++) Equal(lines[i], longer[i], $"line {i} is the same");
            var text = DumpEvidence.Build(TopLevelHeadLong, Session).Text;
            False(text.Contains("Steam name", StringComparison.Ordinal), "line 17");
            False(text.Contains(@"C:\Users", StringComparison.OrdinalIgnoreCase), "line 28");
        });

        Test("the rows are the session's PALADIN lines verbatim with their clocks, and nothing else", () =>
        {
            var e = DumpEvidence.Build(TopLevelHead, Session);
            var expected = SessionLines.Where(l => l.Contains("   PALADIN", StringComparison.Ordinal)).ToList();
            Equal(10, expected.Count, "HELLO, ENV, DEF, BEGIN, 2 PLAYER, 3 rows, DONE");
            Equal(expected.Count, e.RowLines.Count);
            for (var i = 0; i < expected.Count; i++) Equal(expected[i], e.RowLines[i], $"row line {i}");
            Equal(string.Join("\n", expected), e.Rows);
            Equal(e.Header + "\n" + e.Rows, e.Text);
            Equal(Encoding.UTF8.GetByteCount(e.Text), (int)e.Bytes);

            Equal(3, e.EntityRows);
            Equal(2021, e.EnvCount);
            False(e.Complete, "3 < 2,021");
            Equal(2, e.Players);
            Equal(1, e.Chunks);
            Equal(0, e.ErrCount);
            True(DumpEvidence.Build(TopLevelHead, SessionComplete).Complete, "rows >= ENV");
        });

        Test("the banker's own expressions read the built text: readDumpHeader, parseDumpLog, parsePlayerRows", () =>
        {
            // Ported verbatim from scripts/world-dump/logic.mjs (lines 117-131, 40-41, 83), which the
            // Worker and run.mjs share. If these stop matching, the launcher is sending something
            // the banker cannot bank.
            var text = DumpEvidence.Build(TopLevelHead, Session).Text;
            Equal("2026-09-18 04:53", Regex.Match(text, @"RelicCardinal started at (\d{4}-\d{2}-\d{2} \d{2}:\d{2})").Groups[1].Value, "startedAt");
            Equal("16.3.11308.0", Regex.Match(text, @"Version \[([\d.]+)\]").Groups[1].Value, "build");
            Equal("-dev -replay playback:AgeIV_Replay_249960029", Regex.Match(text, @"RUN-OPTIONS \[([^\]]*)\]").Groups[1].Value, "runOptions");
            Equal("1176837288", Regex.Match(text, @"MapGen -- Generating with seed (\d+)").Groups[1].Value, "seed");
            Equal("taiga_woodlands_snow", Regex.Match(text, @"MapGen -- Generating with biome (\S+)").Groups[1].Value);
            Equal("highview", Regex.Match(text, @"MapGen -- Generating with layout (\S+)").Groups[1].Value);
            Equal("map_size_416", Regex.Match(text, @"MapGen -- Generating with size (\S+)").Groups[1].Value);
            Equal("2", Regex.Match(text, @"MapGen -- Generating with player count (\d+)").Groups[1].Value, "players");
            Equal("04:54:13", Regex.Match(text, @"^(\d{2}:\d{2}:\d{2})(?:\.\d+)?\s+PALADIN2?\|", RegexOptions.Multiline).Groups[1].Value, "firstRowClock, the session form (logic.mjs:118)");

            const string Coord = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";
            var row = new Regex($@"PALADIN2?\|(\d+)\|(\d+)\|(?:(-?\d+)\|)?([^|\s]+)\|{Coord}\|{Coord}\|{Coord}(?:\|(world|\d+|\?))?");
            Equal(3, text.Split('\n').Count(l => row.IsMatch(l)), "parseDumpLog finds the three rows");
            var player = new Regex(@"PALADIN2_PLAYER\|(\d+)\|(.*)\|([^|]*)\|(-?\d+)\|(-?[\d.eE+-]*)\|(-?[\d.eE+-]*)\s*$");
            var names = text.Split('\n').Select(l => player.Match(l)).Where(m => m.Success).Select(m => m.Groups[2].Value).ToList();
            Equal(2, names.Count, "parsePlayerRows finds both players");
            Equal("May", names[0]);
            Equal("??Creator", names[1]);
        });

        // ---------------------------------------------------------------------------

        Suite("DumpEnvelope (the upload body)");

        Test("serialises compactly in the documented key order, deterministically, and round-trips", () =>
        {
            var envelope = SmallEnvelope();
            var json = envelope.ToJson();

            True(json.StartsWith("{\"v\":1,\"gameId\":246737201,\"launcher\":\"0.4.0-test\",\"session\":\"20260918-045319-abc123\",\"capturedAtUtc\":\"2026-09-18T03:54:13Z\",\"replayBuild\":11308,\"gameVersion\":\"16.3.11308.0\",\"chord\":\"altshift\",\"input\":\"paste\",\"counts\":{\"env\":3,\"rows\":3,\"err\":0,\"players\":2,\"chunks\":1},\"timings\":{\"missionStartS\":19.5,\"helloS\":25.8,\"dumpS\":54.9},\"header\":\"", StringComparison.Ordinal),
                "the §4.2 shape, in order: " + json[..Math.Min(json.Length, 200)]);
            // "rows" is also a key inside counts, so the top-level one is probed as the string it opens.
            var keys = new[] { "\"v\"", "\"gameId\"", "\"launcher\"", "\"session\"", "\"capturedAtUtc\"", "\"replayBuild\"", "\"gameVersion\"", "\"chord\"", "\"input\"", "\"counts\"", "\"timings\"", "\"header\":\"", "\"rows\":\"" };
            var positions = keys.Select(k => json.IndexOf(k, StringComparison.Ordinal)).ToList();
            True(positions.All(p => p >= 0), "every key present");
            True(positions.SequenceEqual(positions.OrderBy(p => p)), "in the documented order");
            False(json.Contains('\n'), "compact: newlines only inside the header and rows strings");

            Equal(json, SmallEnvelope().ToJson(), "the same envelope gives the same bytes");
            Equal(Encoding.UTF8.GetByteCount(json), (int)envelope.SizeBytes);
            True(envelope.WithinCap, "a small dump is far under 1 MiB");

            var back = DumpEnvelope.FromJson(json);
            Equal(envelope.GameId, back.GameId);
            Equal(envelope.Header, back.Header);
            Equal(envelope.Rows, back.Rows);
            Equal(envelope.Counts.Env, back.Counts.Env);
            Equal(envelope.Timings.DumpS, back.Timings.DumpS);
            Equal(envelope.ReplayBuild, back.ReplayBuild);
            Equal(json, back.ToJson(), "and serialises back identically");
        });

        Test("is built from the evidence: counts, build, the header and the rows as text", () =>
        {
            var evidence = DumpEvidence.Build(TopLevelHead, Session);
            var envelope = DumpEnvelope.From(evidence, 249960029, "0.4.0", "s", new DateTime(2026, 9, 18, 3, 54, 44, DateTimeKind.Utc), null, "altshift", "paste");
            Equal(1, envelope.V);
            Equal(249960029L, envelope.GameId);
            Equal(2021, envelope.Counts.Env);
            Equal(3, envelope.Counts.Rows);
            Equal(2, envelope.Counts.Players);
            Equal(1, envelope.Counts.Chunks);
            Equal("16.3.11308.0", envelope.GameVersion);
            Equal(null, envelope.ReplayBuild, "unreadable replay header: null, not zero");
            Equal(evidence.Header, envelope.Header);
            Equal(evidence.Rows, envelope.Rows);
            Equal("2026-09-18T03:54:44Z", envelope.CapturedAtUtc);
            True(envelope.Rows.StartsWith("04:53:44.792   PALADIN3_HELLO|", StringComparison.Ordinal), "rows keep their clock prefixes");
        });

        Test("a body over 1 MiB is known before it is sent", () =>
        {
            var big = SmallEnvelope();
            big.Rows = new string('x', (int)DumpEnvelope.MaxBytes + 1);
            False(big.WithinCap, "over the cap");
            True(SmallEnvelope().WithinCap, "a small dump is within the cap");
        });

        Test("the UTC stamp is seconds, Z-suffixed, whatever kind of DateTime it was given", () =>
        {
            Equal("2026-09-17T20:05:57Z", DumpJson.Utc(new DateTime(2026, 9, 17, 20, 5, 57, 123, DateTimeKind.Utc)), "milliseconds dropped");
            Equal("2026-09-17T20:05:57Z", DumpJson.Utc(new DateTime(2026, 9, 17, 20, 5, 57, DateTimeKind.Utc)));
        });

        Test("the status and upload answers parse leniently and give the F10 plain words", () =>
        {
            var status = DumpStatus.TryParse("{\"status\":\"user\",\"n\":1310,\"map\":\"West Lake\",\"seed\":\"318943db\",\"uploadedAt\":\"2026-09-18T10:00:00Z\"}")!;
            Equal("user", status.Status);
            Equal(1310, status.N);
            True(status.HasWorld, "a user file holds the slot");
            False(DumpStatus.TryParse("{\"status\":\"none\"}")!.HasWorld, "no world yet");
            Equal(null, DumpStatus.TryParse("<html>"), "not JSON: null, not an exception");
            Equal(null, DumpStatus.TryParse(""));

            var stored = DumpUploadResponse.TryParse("{\"status\":\"stored\",\"gameId\":246737201,\"map\":\"West Lake\",\"seed\":\"318943db\",\"n\":1310,\"bytes\":18212,\"verified\":[\"run_options\",\"complete\",\"first_id\",\"seed\",\"map\",\"tc\",\"orders\"],\"url\":\"/world/246737201.json\"}")!;
            Equal("stored", stored.Status);
            Equal(7, stored.Verified!.Count);
            Equal("/world/246737201.json", stored.Url);
            var rejected = DumpUploadResponse.TryParse("{\"error\":\"seed_mismatch\",\"detail\":\"the log's seed is not the sidecar's\"}")!;
            Equal("seed_mismatch", rejected.Error);

            Equal("this game already has the owner's map", DumpUploadResponse.PlainWords("banked"));
            Equal("someone already sent this game's map", DumpUploadResponse.PlainWords("already"));
            Equal("Paladin has no map data for this game yet", DumpUploadResponse.PlainWords("no_sidecar"));
            Equal("the replay that played is not this game", DumpUploadResponse.PlainWords("wrong_replay"));
            Equal("the replay that played is not this game", DumpUploadResponse.PlainWords("seed_mismatch"));
            Equal("not every object was printed", DumpUploadResponse.PlainWords("incomplete"));
            Equal("too many sends from your address; try again in a minute", DumpUploadResponse.PlainWords("rate_limited"));
            Equal("orders_mismatch: one order names an object that was not printed", DumpUploadResponse.PlainWords("orders_mismatch", "one order names an object that was not printed"), "an unlisted code is shown with its detail");
            Equal("no reason was given", DumpUploadResponse.PlainWords(null));
        });

        Test("dump.json round-trips and is written temp+replace", () =>
        {
            using var dir = new TempDir("dump-record");
            var path = Path.Combine(dir.Path, DumpRecord.FileName);
            var record = new DumpRecord
            {
                Phase = "printing",
                GameId = 249960029,
                SessionId = "20260918-045319-abc123",
                Chord = "altshift",
                InputMethod = "paste",
                EnvCount = 2021,
                GameTimeAtEnv = 6.0,
                DefOk = true,
                Chunks = { new DumpChunkRecord { A = 0, B = 199, Done = true, Count = 200, Tries = 1 } },
                Timings = { MissionStart = new DateTime(2026, 9, 18, 3, 53, 38, DateTimeKind.Utc) },
            };
            record.Save(path);
            record.Phase = "uploading";
            record.Upload.Status = DumpUploadStatus.Pending;
            record.Save(path);
            False(File.Exists(path + ".tmp"), "the temp file is gone after the replace");

            var back = DumpRecord.TryLoad(path)!;
            Equal("uploading", back.Phase);
            Equal(249960029L, back.GameId);
            Equal(2021, back.EnvCount);
            Equal(1, back.Chunks.Count);
            Equal(200, back.Chunks[0].Count);
            Equal(DumpUploadStatus.Pending, back.Upload.Status);
            Equal(new DateTime(2026, 9, 18, 3, 53, 38, DateTimeKind.Utc), back.Timings.MissionStart);
            True(File.ReadAllText(path).Contains("\"status\": \"pending\"", StringComparison.Ordinal), "enums as words, indented");
            Equal(null, DumpRecord.TryLoad(Path.Combine(dir.Path, "missing.json")));
        });

        // ---------------------------------------------------------------------------

        Suite("DumpUploader (no network)");

        Test("POSTs the envelope as application/json to /api/world/<id> with the version headers", () =>
        {
            var handler = new StubHandler(HttpStatusCode.OK, "{\"status\":\"stored\",\"gameId\":246737201,\"map\":\"West Lake\",\"seed\":\"318943db\",\"n\":1310,\"bytes\":18212,\"verified\":[\"seed\",\"tc\"],\"url\":\"/world/246737201.json\"}");
            var envelope = SmallEnvelope();
            var result = Uploader(handler).UploadAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();

            Equal(DumpUploadOutcome.Stored, result.Outcome);
            True(result.Accepted, "accepted");
            Equal(200, result.HttpCode);
            Equal("West Lake", result.Response!.Map);
            Equal(1310, result.Response.N);
            False(result.RetryLater, "stored needs no retry");

            Equal(1, handler.Requests.Count, "one request");
            var sent = handler.Requests[0];
            Equal("POST", sent.Method);
            Equal("https://paladin.example/api/world/246737201", sent.Url);
            Equal("application/json", sent.ContentType, "exactly, no charset suffix");
            Equal("PaladinReplayLauncher/0.4.0-test (+https://github.com/odinmaycall/paladin-replay-launcher)", sent.UserAgent);
            Equal("0.4.0-test", sent.Launcher, DumpUploader.LauncherHeader);
            Equal("application/json", sent.Accept);
            Equal(envelope.ToJson(), sent.Body, "the body is the envelope's deterministic JSON");
        });

        Test("200 already: the slot is taken, nothing stored, and the console gets the plain words", () =>
        {
            var result = Uploader(new StubHandler(HttpStatusCode.OK, "{\"status\":\"already\",\"src\":\"user\",\"n\":1310}"))
                .UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Already, result.Outcome);
            False(result.Accepted, "already is not stored");
            False(result.LandedEarlier, "the first attempt was answered");
            Equal(1, result.Attempts);
            Equal("already", result.Code);
            Equal("someone already sent this game's map", result.PlainWords);
            Equal("user", result.Response!.Src);
        });

        Test("every 4xx of §4.3 maps to its outcome and its plain words, and is never retried", () =>
        {
            var cases = new (HttpStatusCode Code, string Body, DumpUploadOutcome Outcome, string Words)[]
            {
                (HttpStatusCode.Conflict, "{\"error\":\"banked\"}", DumpUploadOutcome.Banked, "this game already has the owner's map"),
                (HttpStatusCode.NotFound, "{\"error\":\"no_sidecar\"}", DumpUploadOutcome.NoSidecar, "Paladin has no map data for this game yet"),
                (HttpStatusCode.BadRequest, "{\"error\":\"seed_mismatch\",\"detail\":\"log seed 1 is not sidecar seed 2\"}", DumpUploadOutcome.Rejected, "the replay that played is not this game"),
                (HttpStatusCode.BadRequest, "{\"error\":\"incomplete\",\"detail\":\"1200 of 1465 rows\"}", DumpUploadOutcome.Rejected, "not every object was printed"),
                (HttpStatusCode.BadRequest, "{\"error\":\"personal_lines\",\"detail\":\"a USER line\"}", DumpUploadOutcome.Rejected, "personal_lines: a USER line"),
                (HttpStatusCode.RequestEntityTooLarge, "{\"error\":\"too_large\"}", DumpUploadOutcome.TooLarge, "the map is larger than Paladin accepts"),
                (HttpStatusCode.UnsupportedMediaType, "{\"error\":\"unsupported_media_type\"}", DumpUploadOutcome.UnsupportedMedia, "unsupported_media_type"),
                (HttpStatusCode.TooManyRequests, "{\"error\":\"rate_limited\"}", DumpUploadOutcome.RateLimited, "too many sends from your address; try again in a minute"),
                (HttpStatusCode.MethodNotAllowed, "{\"error\":\"method_not_allowed\"}", DumpUploadOutcome.MethodNotAllowed, "method_not_allowed"),
            };
            foreach (var c in cases)
            {
                var handler = new StubHandler(c.Code, c.Body);
                var result = Uploader(handler).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
                Equal(c.Outcome, result.Outcome, $"{(int)c.Code} {c.Body}");
                Equal((int)c.Code, result.HttpCode);
                Equal(c.Words, result.PlainWords, $"{(int)c.Code} words");
                False(result.Accepted, "not accepted");
                False(result.RetryLater, "a 4xx is final; the rows are not re-sent");
                Equal(1, handler.Requests.Count, "exactly one request");
            }
        });

        Test("503 uploads_closed is final after one attempt; any other 5xx is retried twice (5 s apart by default), then kept for later (F11)", () =>
        {
            Equal(3, DumpUploader.MaxAttempts, "§3.2: one POST and two retries");
            Equal(5, DumpUploader.RetryDelaySeconds, "§3.2: 5 s back-off");
            Equal(TimeSpan.FromSeconds(5), new DumpUploader("https://paladin.example/api/world/", "x", Quiet()).RetryDelay, "the production default is the design's");

            var closed = new StubHandler(HttpStatusCode.ServiceUnavailable, "{\"error\":\"uploads_closed\"}");
            var result = Uploader(closed).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.UploadsClosed, result.Outcome);
            True(result.RetryLater, "kept for --dump-upload");
            Equal(1, closed.Requests.Count, "the KV binding is absent: no retry can help");
            Equal(1, result.Attempts);

            foreach (var (code, body) in new[] { (HttpStatusCode.ServiceUnavailable, "<html>cloudflare</html>"), (HttpStatusCode.InternalServerError, "oops"), (HttpStatusCode.BadGateway, "") })
            {
                var handler = new StubHandler(code, body);
                var r = Uploader(handler).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
                Equal(DumpUploadOutcome.ServerError, r.Outcome, $"{(int)code} {body}: a bare 503 is not the Worker's uploads_closed");
                True(r.RetryLater, "retry later");
                Equal(3, handler.Requests.Count, $"{(int)code}: one POST and two retries");
                Equal(3, r.Attempts);
                False(r.LandedEarlier, "the Worker answered every time");
                False(r.Accepted, "not accepted");
            }
        });

        Test("a 5xx or a network failure followed by stored on a retry is Stored, with the same envelope each time", () =>
        {
            var handler = StubHandler.Sequence(
                () => StubHandler.Json(HttpStatusCode.BadGateway, ""),
                () => throw new HttpRequestException("The connection was reset."),
                () => StubHandler.Json(HttpStatusCode.OK, "{\"status\":\"stored\",\"n\":3}"));
            var result = Uploader(handler).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Stored, result.Outcome);
            True(result.Accepted, "accepted on the third attempt");
            False(result.LandedEarlier, "stored is stored");
            Equal(3, result.Attempts);
            Equal(3, handler.Requests.Count);
            Equal(handler.Requests[0].Body, handler.Requests[2].Body, "the same envelope each time");
            Equal("POST", handler.Requests[2].Method);
        });

        Test("a network failure is Unreachable after three attempts, and a timeout is too", () =>
        {
            var failing = new StubHandler((_, _) => throw new HttpRequestException("No such host is known."));
            var result = Uploader(failing).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Unreachable, result.Outcome);
            True(result.RetryLater, "kept for --dump-upload");
            True(result.Error!.Contains("No such host", StringComparison.Ordinal), "the reason is kept");
            Equal(3, failing.Requests.Count);
            Equal(3, result.Attempts);

            var slow = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return StubHandler.Json(HttpStatusCode.OK, "{}"); });
            var timed = Uploader(slow, uploadTimeout: TimeSpan.FromMilliseconds(50))
                .UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Unreachable, timed.Outcome);
            True(timed.Error!.Contains("no answer within", StringComparison.Ordinal), timed.Error);
            Equal(3, slow.Requests.Count);
        });

        Test("already on a retry after an attempt that was sent and never answered is this run's own upload, not F10", () =>
        {
            const string already = "{\"status\":\"already\",\"src\":\"user\",\"n\":3}";
            var calls = 0;
            var handler = new StubHandler(async (_, ct) =>
            {
                if (calls++ == 0) await Task.Delay(TimeSpan.FromSeconds(30), ct);   // the first POST lands; its answer never comes
                return StubHandler.Json(HttpStatusCode.OK, already);
            });
            var result = Uploader(handler, uploadTimeout: TimeSpan.FromMilliseconds(50))
                .UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Already, result.Outcome, "the wire answer is kept as it was");
            True(result.LandedEarlier, "the timed-out POST is the one the Worker stored");
            True(result.Accepted, "so the console prints the success line");
            Equal(2, result.Attempts);
            False(result.RetryLater, "nothing to re-send");

            // The line cut under the answer's body, after the send, is the same case.
            var cut = StubHandler.Sequence(() => StubHandler.Cut(), () => StubHandler.Json(HttpStatusCode.OK, already));
            var afterCut = Uploader(cut).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            True(afterCut.Accepted && afterCut.LandedEarlier, "sent, answer lost, then already");

            // A failure before anything was sent is not: someone else holds the slot.
            var dns = StubHandler.Sequence(
                () => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
                () => StubHandler.Json(HttpStatusCode.OK, already));
            var notOurs = Uploader(dns).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Already, notOurs.Outcome);
            False(notOurs.LandedEarlier, "nothing of ours reached the Worker");
            False(notOurs.Accepted, "F10: someone already sent this game's map");
            Equal("someone already sent this game's map", notOurs.PlainWords);

            // A 5xx is an answer: the Worker saw the POST and did not store it.
            var afterServerError = Uploader(StubHandler.Sequence(() => StubHandler.Json(HttpStatusCode.InternalServerError, ""), () => StubHandler.Json(HttpStatusCode.OK, already)))
                .UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            False(afterServerError.LandedEarlier, "answered, not lost");

            True(DumpUploader.FailedBeforeSending(new HttpRequestException(HttpRequestError.ConnectionError, "refused")), "connect");
            True(DumpUploader.FailedBeforeSending(new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")), "tls");
            False(DumpUploader.FailedBeforeSending(new HttpRequestException(HttpRequestError.ResponseEnded, "ended")), "after the send");
            False(DumpUploader.FailedBeforeSending(new HttpRequestException("unknown")), "unknown is not assumed unsent");
        });

        Test("the line cut under the answer's body is Unreachable for the upload and a warning for the pre-flight, never a throw", () =>
        {
            var cut = new StubHandler((_, _) => Task.FromResult(StubHandler.Cut()));
            var result = Uploader(cut).UploadAsync(SmallEnvelope(), CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.Unreachable, result.Outcome);
            True(result.RetryLater, "F11");
            True(result.Error!.Contains("cut short", StringComparison.Ordinal), result.Error);
            Equal(3, cut.Requests.Count, "retried like any network failure");

            var pre = Uploader(cut).CheckAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            False(pre.Reachable, "no answer");
            False(pre.HasWorld, "unknown is not a stop");
            True(pre.Error!.Contains("cut short", StringComparison.Ordinal), pre.Error);
            Equal(4, cut.Requests.Count, "the pre-flight is never retried");
        });

        Test("Ctrl+C during the back-off stops the retries", () =>
        {
            using var cts = new CancellationTokenSource();
            var failing = new StubHandler((_, _) => throw new HttpRequestException("reset"));
            var task = Uploader(failing, retryDelay: TimeSpan.FromSeconds(30)).UploadAsync(SmallEnvelope(), cts.Token);
            cts.CancelAfter(100);
            Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult(), "cancellation propagates to the runner");
            Equal(1, failing.Requests.Count, "no POST after the cancel");
        });

        Test("the user's own Ctrl+C is not swallowed as a network failure", () =>
        {
            using var cts = new CancellationTokenSource();
            var slow = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return StubHandler.Json(HttpStatusCode.OK, "{}"); });
            var task = Uploader(slow).UploadAsync(SmallEnvelope(), cts.Token);
            cts.Cancel();
            Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult(), "cancellation propagates to the runner");
        });

        Test("an envelope over 1 MiB is refused locally and nothing is sent", () =>
        {
            var handler = new StubHandler(HttpStatusCode.OK, "{\"status\":\"stored\"}");
            var big = SmallEnvelope();
            big.Rows = new string('x', (int)DumpEnvelope.MaxBytes);
            var result = Uploader(handler).UploadAsync(big, CancellationToken.None).GetAwaiter().GetResult();
            Equal(DumpUploadOutcome.TooLarge, result.Outcome);
            Equal(null, result.HttpCode, "no request was made");
            Equal(0, handler.Requests.Count);
        });

        Test("a 200 that is not the stored/already shape is Unexpected, not Accepted", () =>
        {
            var result = DumpUploader.Classify(200, "OK", "<html>a captive portal</html>");
            Equal(DumpUploadOutcome.Unexpected, result.Outcome);
            False(result.Accepted, "unexpected is not accepted");
            Equal(null, result.Response);
            True(result.Body!.Length <= 512, "the body is kept short");
        });

        Test("the pre-flight GET reads the status shape and never stops a dump on its own failure", () =>
        {
            var handler = new StubHandler(HttpStatusCode.OK, "{\"status\":\"owner\",\"n\":1465,\"map\":\"West Lake\",\"seed\":\"318943db\"}");
            var pre = Uploader(handler).CheckAsync(246737201, CancellationToken.None).GetAwaiter().GetResult();
            True(pre.Reachable, "reachable");
            True(pre.HasWorld, "the owner's file exists: the dump would be refused");
            Equal("owner", pre.Status!.Status);
            Equal(1465, pre.Status.N);
            Equal("GET", handler.Requests[0].Method);
            Equal("https://paladin.example/api/world/246737201", handler.Requests[0].Url);
            Equal("0.4.0-test", handler.Requests[0].Launcher, "the version header travels on the GET too");
            Equal(null, handler.Requests[0].Body, "no body");

            var none = Uploader(new StubHandler(HttpStatusCode.OK, "{\"status\":\"none\"}")).CheckAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            True(none.Reachable && !none.HasWorld, "no world yet: go ahead");

            var broken = Uploader(new StubHandler(HttpStatusCode.InternalServerError, "oops")).CheckAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            True(broken.Reachable, "answered, if badly");
            False(broken.HasWorld, "no status from a 500");
            Equal(500, broken.HttpCode);

            var down = Uploader(new StubHandler((_, _) => throw new HttpRequestException("refused"))).CheckAsync(1, CancellationToken.None).GetAwaiter().GetResult();
            False(down.Reachable, "no answer at all");
            False(down.HasWorld, "unknown is not a stop");
            NotNull(down.Error);
        });

        Test("the target is the configured base plus the digits, and the base must be http(s) with no query", () =>
        {
            Equal("https://paladin.odinmaycall.com/api/world/246737201", DumpUploader.TargetFor("https://paladin.odinmaycall.com/api/world/", 246737201).ToString());
            Equal("https://paladin.odinmaycall.com/api/world/246737201", DumpUploader.TargetFor("https://paladin.odinmaycall.com/api/world", 246737201).ToString(), "a missing trailing slash is added");
            Equal("http://localhost:8787/api/world/5", DumpUploader.TargetFor("http://localhost:8787/api/world/", 5).ToString(), "wrangler dev");
            Equal(DumpUploader.DefaultBaseUrl, new LauncherConfig().DumpUploadBaseUrl, "the config default is the uploader's");
            Throws<ArgumentException>(() => DumpUploader.BaseUri("file:///C:/x/"), "no file://");
            Throws<ArgumentException>(() => DumpUploader.BaseUri("https://paladin.example/api/world/?x=1"), "no query");
            Throws<ArgumentException>(() => DumpUploader.BaseUri("not a url"));
            Throws<ArgumentOutOfRangeException>(() => DumpUploader.TargetFor("https://paladin.example/api/world/", 0), "zero is not a game id");
            Throws<ArgumentOutOfRangeException>(() => DumpUploader.TargetFor("https://paladin.example/api/world/", -1));
        });

        // ---------------------------------------------------------------------------

        Suite("PaladinUri (dump links)");

        Test("parses a dump link: game= and the url= repeats, as strictly as replay", () =>
        {
            const string A = "https://api.ageofempires.com/api/GameStats/AgeIV/GetMatchReplay/?matchId=246737201&profileId=2848144";
            const string B = "https://api.ageofempires.com/api/GameStats/AgeIV/GetMatchReplay/?matchId=246737201&profileId=11018483";
            var link = PaladinUri.BuildDumpLink(246737201, new[] { A, B });
            True(link.StartsWith("paladin://dump?game=246737201&url=", StringComparison.Ordinal), link);

            var result = PaladinUri.Parse(link);
            True(result.Ok, result.Error ?? "");
            True(result.IsDump, "a dump");
            Equal(PaladinUri.DumpAction, result.Action);
            Equal(246737201L, result.GameId);
            Equal("url", result.Request!.Kind);
            Equal(A, result.Request.Value, "first candidate is primary");
            Equal(1, result.Request.Fallbacks.Count);
            Equal(B, result.Request.Fallbacks[0]);
        });

        Test("a dump link needs a plain game id of at most 15 digits", () =>
        {
            const string Url = "&url=https%3A%2F%2Fe.com%2Fa.rec";
            False(PaladinUri.Parse("paladin://dump?url=https%3A%2F%2Fe.com%2Fa.rec").Ok, "no game=");
            True(PaladinUri.Parse("paladin://dump?url=https%3A%2F%2Fe.com%2Fa.rec").Error!.Contains("game=", StringComparison.Ordinal), "says so");
            False(PaladinUri.Parse("paladin://dump?game=abc" + Url).Ok, "letters");
            False(PaladinUri.Parse("paladin://dump?game=0" + Url).Ok, "zero");
            False(PaladinUri.Parse("paladin://dump?game=-5" + Url).Ok, "negative");
            False(PaladinUri.Parse("paladin://dump?game=1234567890123456" + Url).Ok, "16 digits");
            True(PaladinUri.Parse("paladin://dump?game=123456789012345" + Url).Ok, "15 digits");
            False(PaladinUri.Parse("paladin://dump?game=1&game=2" + Url).Ok, "a repeated game= is refused, not guessed at");
            False(PaladinUri.Parse("paladin://dump?game=246737201").Ok, "no replay to fetch");
            False(PaladinUri.Parse("paladin://dump?game=246737201&id=246737201").Ok, "the archive-id form has no provider");
            False(PaladinUri.Parse("paladin://dump/246737201").Ok, "nor the short form");
        });

        Test("a dump link can name a local replay, and rejects a non-http url exactly as replay does", () =>
        {
            var local = PaladinUri.Parse("paladin://dump?game=246737201&path=C%3A%5Ctemp%5CAgeIV_Replay_246737201");
            True(local.Ok, local.Error ?? "");
            Equal("local", local.Request!.Kind);
            Equal(@"C:\temp\AgeIV_Replay_246737201", local.Request.Value);
            Equal(246737201L, local.GameId);

            False(PaladinUri.Parse("paladin://dump?game=1&url=file%3A%2F%2F%2FC%3A%2FWindows%2Fevil.exe").Ok, "no file://");
            var mixed = "paladin://dump?game=1&url=" + Uri.EscapeDataString("https://good.example/a.rec") + "&url=" + Uri.EscapeDataString("ftp://bad.example/a.rec");
            False(PaladinUri.Parse(mixed).Ok, "one bad candidate poisons the link");
        });

        Test("a link naming an upload host is ignored: the target never travels in the link", () =>
        {
            var link = "paladin://dump?game=246737201&url=" + Uri.EscapeDataString("https://good.example/a.rec")
                     + "&upload=" + Uri.EscapeDataString("https://evil.example/api/world/")
                     + "&host=evil.example&target=" + Uri.EscapeDataString("http://evil.example/");
            var result = PaladinUri.Parse(link);
            True(result.Ok, result.Error ?? "");
            Equal(246737201L, result.GameId);
            Equal(1, result.Request!.AllCandidates().Count(), "only the replay url");
            False(result.Request.AllCandidates().Any(u => u.Contains("evil", StringComparison.Ordinal)), "nothing from the extra parameters");
            False((result.Request.SuggestedName ?? "").Contains("evil", StringComparison.Ordinal), "no name from the extra parameters");
        });

        Test("replay links parse exactly as before: action 'replay', no game id, same request", () =>
        {
            var url = PaladinUri.Parse("paladin://replay?url=https%3A%2F%2Fexample.com%2Freplay.rec");
            True(url.Ok, url.Error ?? "");
            Equal(PaladinUri.ReplayAction, url.Action);
            False(url.IsDump, "a replay link is not a dump");
            Equal(null, url.GameId);
            Equal("url", url.Request!.Kind);
            Equal("https://example.com/replay.rec", url.Request.Value);

            var withGame = PaladinUri.Parse("paladin://replay?url=https%3A%2F%2Fexample.com%2Freplay.rec&game=5");
            True(withGame.Ok, "still a valid replay link");
            Equal(null, withGame.GameId, "game= means nothing to a replay link");

            var path = PaladinUri.Parse("paladin://replay?path=C%3A%5Ctemp%5Cgame.rec");
            Equal("local", path.Request!.Kind);
            Equal(PaladinUri.ReplayAction, path.Action);

            var shortForm = PaladinUri.Parse("paladin://replay/244989270");
            Equal("aoe4replays", shortForm.Request!.Kind);
            Equal("244989270", shortForm.Request.Value);
            Equal(PaladinUri.ReplayAction, shortForm.Action);

            False(PaladinUri.Parse("paladin://install?url=https%3A%2F%2Fe.com%2Fx").Ok, "an unknown action is still refused");
            True(PaladinUri.Parse("paladin://install?url=https%3A%2F%2Fe.com%2Fx").Error!.Contains("'replay' and 'dump'", StringComparison.Ordinal), "and the message names both");
        });

        Test("the action is readable before the full parse, so the command line can route a bare link", () =>
        {
            Equal("dump", PaladinUri.ActionOf("paladin://dump?game=1&url=https%3A%2F%2Fe.com%2Fa"));
            Equal("dump", PaladinUri.ActionOf("PALADIN://Dump?game=1"), "case-insensitive");
            Equal("replay", PaladinUri.ActionOf("paladin://replay/244989270"));
            Equal("replay", PaladinUri.ActionOf("\"paladin://replay?url=x\""), "quotes trimmed as Parse does");
            Equal("install", PaladinUri.ActionOf("paladin://install"), "whatever it says; Parse decides");
            Equal(null, PaladinUri.ActionOf(@"C:\replays\a.rec"), "not a link");
            Equal(null, PaladinUri.ActionOf("https://example.com/replay"), "another scheme");
        });

        Test("game ids are digits, 1-15 of them, positive", () =>
        {
            True(PaladinUri.TryParseGameId("246737201", out var id), "a real game id");
            Equal(246737201L, id);
            True(PaladinUri.TryParseGameId("1", out _), "one digit");
            True(PaladinUri.TryParseGameId("123456789012345", out _), "15 digits");
            False(PaladinUri.TryParseGameId("1234567890123456", out _), "16 digits");
            False(PaladinUri.TryParseGameId("0", out _), "zero");
            False(PaladinUri.TryParseGameId("", out _), "empty");
            False(PaladinUri.TryParseGameId(null, out _), "null");
            False(PaladinUri.TryParseGameId(" 5", out _), "no whitespace");
            False(PaladinUri.TryParseGameId("+5", out _), "no sign");
            False(PaladinUri.TryParseGameId("5.0", out _), "a decimal");
            False(PaladinUri.TryParseGameId("٥", out _), "ASCII digits only");
        });

        // ---------------------------------------------------------------------------

        Suite("LauncherConfig (dump knobs)");

        Test("the dump knobs default to the design's numbers", () =>
        {
            var c = new LauncherConfig();
            Equal("https://paladin.odinmaycall.com/api/world/", c.DumpUploadBaseUrl);
            Equal(180, c.DumpMissionTimeoutSeconds);
            Equal(DumpLadder.ChunkSize, c.DumpChunkSize);
            Equal(15, c.DumpCloseGraceSeconds, "D1");
            Equal(true, c.DumpEndProcess, "D1");
            Equal(1200, c.DumpChordSettleMs);
            Equal(800, c.DumpAfterPasteMs);
            Equal(600, c.DumpAfterEnterMs);
            Equal(3000, c.DumpHudSettleMs);
            Equal(6, c.DumpHelloWaitSeconds);
            Equal(8, c.DumpDefWaitSeconds);
            Equal(30, c.DumpChunkWaitSeconds);
        });

        Test("a 0.3.0 config.json without the knobs loads with the defaults, schema unchanged", () =>
        {
            var c = LauncherConfig.FromJson("{\"SchemaVersion\":1,\"SteamAppId\":1466860,\"UseDevFlag\":true}");
            Equal(1, c.SchemaVersion);
            Equal(LauncherConfig.CurrentSchemaVersion, c.SchemaVersion, "no bump for added knobs");
            Equal("https://paladin.odinmaycall.com/api/world/", c.DumpUploadBaseUrl);
            Equal(200, c.DumpChunkSize);

            var overridden = LauncherConfig.FromJson("{\"SchemaVersion\":1,\"DumpUploadBaseUrl\":\"http://localhost:8787/api/world/\",\"DumpEndProcess\":false}");
            Equal("http://localhost:8787/api/world/", overridden.DumpUploadBaseUrl, "wrangler dev");
            Equal(false, overridden.DumpEndProcess);
            True(new LauncherConfig().ToJson().Contains("\"DumpUploadBaseUrl\"", StringComparison.Ordinal), "written to config.json on first run");
        });
    }
}
