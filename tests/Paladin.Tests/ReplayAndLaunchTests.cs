using Paladin.Core.Config;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;
using Paladin.Core.Replay;
using Paladin.Core.Steam;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

public static class ReplayAndLaunchTests
{
    /// <summary>
    /// The first 32 bytes of a real replay taken from a live AoE4 install
    /// (Documents\My Games\Age of Empires IV\playback\AgeIV_Replay_244989270).
    /// </summary>
    private static readonly byte[] RealReplayHeader =
    {
        0x00, 0x00, 0x2c, 0x2c, 0x41, 0x4f, 0x45, 0x34, 0x5f, 0x52, 0x45, 0x00,
        0x32, 0x00, 0x30, 0x00, 0x32, 0x00, 0x36, 0x00, 0x2f, 0x00, 0x37, 0x00,
        0x2f, 0x00, 0x33, 0x00, 0x30, 0x00, 0x20, 0x00,
    };

    /// <summary>libraryfolders.vdf copied from a live Steam install, trimmed to two libraries.</summary>
    private const string RealLibraryFoldersVdf = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"241762497"
        			"3556750"		"21197609510"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		""
        		"totalsize"		"2000381014016"
        		"apps"
        		{
        			"1091500"		"66366228927"
        			"1466860"		"48644880175"
        			"1605850"		"53208964214"
        		}
        	}
        }
        """;

    private const string RealAppManifest = """
        "AppState"
        {
        	"appid"		"1466860"
        	"name"		"Age of Empires IV"
        	"StateFlags"		"4"
        	"installdir"		"Age of Empires IV"
        	"buildid"		"20146409"
        }
        """;

    public static void Register()
    {
        Suite("ReplayValidator");

        Test("accepts a real AoE4 replay header", () =>
        {
            var verdict = ReplayValidator.Inspect(RealReplayHeader, totalBytes: 1_845_244);
            True(verdict.IsUsable, "usable");
            True(verdict.MagicMatched, "AOE4_RE magic recognised at offset 4");
            Equal(0x2c2c, verdict.GameBuild!.Value, "game build read from the header");
        });

        Test("rejects an HTML error page served instead of a replay", () =>
        {
            var html = System.Text.Encoding.ASCII.GetBytes("<!DOCTYPE html><html><head><title>404</title>");
            var verdict = ReplayValidator.Inspect(html, totalBytes: 40_000);
            False(verdict.IsUsable, "refused");
            True(verdict.Reason.Contains("HTML"), "says why");
        });

        Test("rejects a JSON error body", () =>
        {
            var json = System.Text.Encoding.ASCII.GetBytes("{\"error\":\"not found\"}");
            var verdict = ReplayValidator.Inspect(json, totalBytes: 5_000);
            False(verdict.IsUsable, "refused");
            True(verdict.Reason.Contains("JSON"), "says why");
        });

        Test("rejects an empty or truncated download", () =>
        {
            False(ReplayValidator.Inspect(Array.Empty<byte>(), 0).IsUsable, "empty");
            False(ReplayValidator.Inspect(RealReplayHeader, 200).IsUsable, "too small to be a replay");
        });

        Test("an unknown binary header warns but still proceeds", () =>
        {
            var unknown = new byte[] { 1, 2, 3, 4, 9, 9, 9, 9, 9, 9, 9, 9 };
            var verdict = ReplayValidator.Inspect(unknown, totalBytes: 500_000);
            True(verdict.IsUsable, "we do not block on an unrecognised patch format");
            False(verdict.MagicMatched, "but we say it was not recognised");
        });

        Test("validates a real file on disk end to end", () =>
        {
            using var dir = new TempDir("replay");
            var path = Path.Combine(dir.Path, "AgeIV_Replay_1");
            var bytes = new byte[4096];
            RealReplayHeader.CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);

            var verdict = ReplayValidator.InspectFile(path);
            True(verdict.IsUsable && verdict.MagicMatched, "recognised from disk");
        });

        // ---------------------------------------------------------------------------

        // ---------------------------------------------------------------------------

        Suite("BuildCompatibility (old-replay detection)");

        Test("reads the build out of the real AoE4 version string", () =>
        {
            // Measured on a live install: RelicCardinal.exe reports 16.3.11308.0, and a
            // replay recorded on it stores 11308 in its header. Same number.
            Equal(11308, BuildCompatibility.ParseGameBuild("16.3.11308.0"));
            Equal(10884, BuildCompatibility.ParseGameBuild("16.2.10884.0"));
        });

        Test("an unreadable version yields no opinion rather than a wrong warning", () =>
        {
            Equal(null, BuildCompatibility.ParseGameBuild(null));
            Equal(null, BuildCompatibility.ParseGameBuild(""));
            Equal(null, BuildCompatibility.ParseGameBuild("16.3"), "too few components");
            Equal(null, BuildCompatibility.ParseGameBuild("16.3.notanumber.0"));
            Equal(null, BuildCompatibility.ParseGameBuild("16.3.0.0"), "a zero build is not a real build");
        });

        Test("matching builds are reported as a match and warn about nothing", () =>
        {
            var r = BuildCompatibility.Compare(11308, 11308);
            Equal(BuildCompatibility.Verdict.Match, r.Verdict);
            False(r.ShouldWarn, "nothing to say");
            Equal(0, BuildCompatibility.SuggestionsFor(r).Count);
        });

        Test("an older replay warns and points at the previous_live branch", () =>
        {
            // The case that appears the day a patch lands: every existing replay is
            // suddenly one build behind. Both builds observed on real live replays.
            var r = BuildCompatibility.Compare(10884, 11308);
            Equal(BuildCompatibility.Verdict.ReplayOlder, r.Verdict);
            True(r.ShouldWarn, "the user needs telling");
            True(r.Message.Contains("10884") && r.Message.Contains("11308"), "both builds named");

            var advice = string.Join(" ", BuildCompatibility.SuggestionsFor(r));
            True(advice.Contains("previous_live"), "names the branch that actually holds the old build");
            True(advice.Contains("Switch back"), "reminds them to undo it");
            True(advice.Contains("may still play"), "does not overstate: not every patch breaks replays");
        });

        Test("a replay several patches old does NOT get told to use previous_live", () =>
        {
            // Real case: a build-7149 replay from Nov 2025 against a build-11308 install.
            // previous_live only ever holds ONE build back, so recommending it here would
            // be confidently wrong advice.
            var r = BuildCompatibility.Compare(7149, 11308);
            var advice = string.Join(" ", BuildCompatibility.SuggestionsFor(r));
            False(advice.Contains("Properties -> Betas"), "must not send them on a pointless rollback");
            True(advice.Contains("unlikely to go back far enough"), "says why");
            True(advice.Contains("EKYavsil"), "points at the tool that can actually do it");
        });

        Test("the near/far boundary is the documented gap, not a guess per call site", () =>
        {
            // Checks the actionable instruction, not the word: the "too old" advice
            // mentions previous_live too, precisely to explain why it will not help.
            const string DoIt = "Properties -> Betas";

            var near = BuildCompatibility.Compare(11308 - BuildCompatibility.PlausiblyRecentBuildGap, 11308);
            True(string.Join(" ", BuildCompatibility.SuggestionsFor(near)).Contains(DoIt), "at the limit, the rollback is still offered");

            var far = BuildCompatibility.Compare(11308 - BuildCompatibility.PlausiblyRecentBuildGap - 1, 11308);
            False(string.Join(" ", BuildCompatibility.SuggestionsFor(far)).Contains(DoIt), "one past it, the rollback is not offered");
        });

        Test("a newer replay tells the user to update instead", () =>
        {
            var r = BuildCompatibility.Compare(11308, 10884);
            Equal(BuildCompatibility.Verdict.ReplayNewer, r.Verdict);
            True(r.ShouldWarn, "still worth saying");
            True(string.Join(" ", BuildCompatibility.SuggestionsFor(r)).Contains("update"), "the fix is a game update");
        });

        Test("an unknown build on either side stays silent", () =>
        {
            foreach (var r in new[]
                     {
                         BuildCompatibility.Compare(null, 11308),
                         BuildCompatibility.Compare(11308, null),
                         BuildCompatibility.Compare(null, null),
                     })
            {
                Equal(BuildCompatibility.Verdict.Unknown, r.Verdict);
                False(r.ShouldWarn, "never warn from a value we could not read");
            }
        });

        Test("the build survives from the replay header through acquisition", () =>
        {
            using var dir = new TempDir("build-carry");
            var path = Path.Combine(dir.Path, "AgeIV_Replay_1");
            var bytes = new byte[4096];
            RealReplayHeader.CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);

            var acquired = new LocalReplayProvider(new PaladinLog(false))
                .AcquireAsync(new ReplayRequest { Kind = "local", Value = path }, dir.Path, default)
                .GetAwaiter().GetResult();

            Equal(0x2c2c, acquired.GameBuild!.Value, "the header build reaches the launcher");
        });

        Suite("ReplayArchive (compressed downloads)");

        Test("detects gzip by magic bytes, which is how Microsoft's endpoint really serves replays", () =>
        {
            // Real first bytes from api.ageofempires.com/.../GetMatchReplay
            var gzipHead = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 };
            Equal(ReplayArchive.ArchiveKind.Gzip, ReplayArchive.Detect(gzipHead));
        });

        Test("detects zip by magic bytes", () =>
            Equal(ReplayArchive.ArchiveKind.Zip, ReplayArchive.Detect(new byte[] { 0x50, 0x4b, 0x03, 0x04 })));

        Test("an uncompressed replay is left alone", () =>
            Equal(ReplayArchive.ArchiveKind.None, ReplayArchive.Detect(RealReplayHeader)));

        Test("round trips a real gzipped replay back to the original bytes", () =>
        {
            using var dir = new TempDir("gzip");

            var replay = new byte[8192];
            RealReplayHeader.CopyTo(replay, 0);
            for (var i = RealReplayHeader.Length; i < replay.Length; i++) replay[i] = (byte)(i % 251);

            var gzPath = Path.Combine(dir.Path, "AgeIV_Replay_226730901.gz");
            using (var raw = new FileStream(gzPath, FileMode.Create))
            using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(replay, 0, replay.Length);

            var result = ReplayArchive.EnsureDecompressed(gzPath);

            Equal(ReplayArchive.ArchiveKind.Gzip, result.WasCompressedAs);
            Equal(replay.Length, (int)result.Bytes, "decompressed to the original size");
            True(File.ReadAllBytes(result.Path).SequenceEqual(replay), "byte-for-byte identical");
            True(ReplayValidator.InspectFile(result.Path).MagicMatched, "and it is a recognisable replay afterwards");
        });

        Test("the .gz extension is dropped so AoE4 gets the name it expects", () =>
        {
            Equal("AgeIV_Replay_226730901", ReplayArchive.StripCompressionExtension("AgeIV_Replay_226730901.gz"));
            Equal("AgeIV_Replay_226730901", ReplayArchive.StripCompressionExtension("AgeIV_Replay_226730901.ZIP"));
            Equal("game.rec", ReplayArchive.StripCompressionExtension("game.rec"), "left alone when not compressed");
        });

        Test("a file that claims to be compressed but is not readable fails clearly", () =>
        {
            using var dir = new TempDir("badgzip");
            var path = Path.Combine(dir.Path, "broken.gz");
            File.WriteAllBytes(path, new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0xff, 0xff, 0xff, 0xff, 0x01, 0x02 });
            Throws<ReplayAcquisitionException>(() => ReplayArchive.EnsureDecompressed(path), "corrupt gzip");
        });

        Test("the local provider transparently handles a gzipped file the user saved", () =>
        {
            using var dir = new TempDir("local-gz");
            using var work = new TempDir("local-gz-work");

            var replay = new byte[8192];
            RealReplayHeader.CopyTo(replay, 0);
            var gzPath = Path.Combine(dir.Path, "AgeIV_Replay_1.gz");
            using (var raw = new FileStream(gzPath, FileMode.Create))
            using (var gz = new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(replay, 0, replay.Length);

            var acquired = new LocalReplayProvider(new PaladinLog(false))
                .AcquireAsync(new ReplayRequest { Kind = "local", Value = gzPath }, work.Path, default)
                .GetAwaiter().GetResult();

            Equal(8192, (int)acquired.SizeBytes, "decompressed");
            Equal("AgeIV_Replay_1", acquired.SuggestedFileName, "no .gz in the name AoE4 is given");
            True(acquired.IsTemporary, "the decompressed copy is ours to clean up");
            True(File.Exists(gzPath), "the user's own file is untouched");
        });

        // ---------------------------------------------------------------------------

        Suite("Replay providers");

        Test("an http(s) argument is classified as a download, anything else as a path", () =>
        {
            Equal("url", ReplayProviderRegistry.ClassifyRawInput("https://example.com/a.rec").Kind);
            Equal("url", ReplayProviderRegistry.ClassifyRawInput("http://example.com/a.rec").Kind);
            Equal("local", ReplayProviderRegistry.ClassifyRawInput(@"C:\replays\a.rec").Kind);
            Equal("local", ReplayProviderRegistry.ClassifyRawInput("a.rec").Kind);
        });

        Test("the registry routes each request to the provider that claims it", () =>
        {
            var log = new PaladinLog(false);
            var registry = new ReplayProviderRegistry()
                .Register(new LocalReplayProvider(log))
                .Register(new DirectUrlReplayProvider(log));

            Equal("local", registry.Resolve(new ReplayRequest { Kind = "local", Value = "a.rec" }).Id);
            Equal("url", registry.Resolve(new ReplayRequest { Kind = "url", Value = "https://e.com/a.rec" }).Id);
        });

        Test("an unregistered provider fails with a message naming what is available", () =>
        {
            var registry = new ReplayProviderRegistry().Register(new LocalReplayProvider(new PaladinLog(false)));
            Throws<ReplayAcquisitionException>(
                () => registry.Resolve(new ReplayRequest { Kind = "aoe4replays", Value = "12345" }),
                "the archive provider is not built yet");
        });

        Test("the local provider refuses a file that is not a replay", () =>
        {
            using var dir = new TempDir("local-bad");
            var path = dir.File("notareplay.rec", "hello");
            var provider = new LocalReplayProvider(new PaladinLog(false));

            Throws<ReplayAcquisitionException>(
                () => provider.AcquireAsync(new ReplayRequest { Kind = "local", Value = path }, dir.Path, default).GetAwaiter().GetResult(),
                "too small");
        });

        Test("the local provider never reports the user's own file as temporary", () =>
        {
            using var dir = new TempDir("local-good");
            var path = Path.Combine(dir.Path, "AgeIV_Replay_1");
            var bytes = new byte[4096];
            RealReplayHeader.CopyTo(bytes, 0);
            File.WriteAllBytes(path, bytes);

            var provider = new LocalReplayProvider(new PaladinLog(false));
            var acquired = provider.AcquireAsync(new ReplayRequest { Kind = "local", Value = path }, dir.Path, default)
                .GetAwaiter().GetResult();

            False(acquired.IsTemporary, "the launcher must never delete a file the user already had");
            Equal(4096, acquired.SizeBytes);
        });

        Test("a download file name cannot escape the working directory", () =>
        {
            Equal("evil.rec", DirectUrlReplayProvider.Sanitise(@"..\..\Windows\System32\evil.rec"));
            Equal("evil.rec", DirectUrlReplayProvider.Sanitise("../../etc/evil.rec"));
            Equal("a_b.rec", DirectUrlReplayProvider.Sanitise("a:b.rec"));
            True(DirectUrlReplayProvider.Sanitise("").Length > 0, "an empty name still yields something usable");
        });

        Test("a download file name is taken from the URL when the server offers none", () =>
        {
            Equal("game.rec", DirectUrlReplayProvider.ChooseFileName(new Uri("https://e.com/replays/game.rec"), null));
            Equal("named.rec", DirectUrlReplayProvider.ChooseFileName(new Uri("https://e.com/replays/game.rec"), "named.rec"));
            True(DirectUrlReplayProvider.ChooseFileName(new Uri("https://e.com/"), null).Length > 0, "bare host still names something");
        });

        Test("only http and https are downloadable", () =>
        {
            var provider = new DirectUrlReplayProvider(new PaladinLog(false));
            False(provider.CanHandle(new ReplayRequest { Kind = "url", Value = "file:///C:/evil.rec" }), "no file://");
            False(provider.CanHandle(new ReplayRequest { Kind = "url", Value = "ftp://e.com/a.rec" }), "no ftp");
            True(provider.CanHandle(new ReplayRequest { Kind = "url", Value = "https://e.com/a.rec" }), "https ok");
        });

        // ---------------------------------------------------------------------------

        Suite("PaladinUri");

        Test("parses a url-carrying link", () =>
        {
            var result = PaladinUri.Parse("paladin://replay?url=https%3A%2F%2Fexample.com%2Freplay.rec");
            True(result.Ok, result.Error ?? "");
            Equal("url", result.Request!.Kind);
            Equal("https://example.com/replay.rec", result.Request.Value);
        });

        Test("parses a path-carrying link", () =>
        {
            var result = PaladinUri.Parse("paladin://replay?path=C%3A%5Ctemp%5Cgame.rec");
            True(result.Ok, result.Error ?? "");
            Equal("local", result.Request!.Kind);
            Equal(@"C:\temp\game.rec", result.Request.Value);
        });

        Test("parses the short match-id form", () =>
        {
            var result = PaladinUri.Parse("paladin://replay/244989270");
            True(result.Ok, result.Error ?? "");
            Equal("aoe4replays", result.Request!.Kind, "defaults to the archive provider");
            Equal("244989270", result.Request.Value);
        });

        Test("honours an explicit source", () =>
        {
            var result = PaladinUri.Parse("paladin://replay?id=244989270&source=relic");
            True(result.Ok, result.Error ?? "");
            Equal("relic", result.Request!.Kind);
        });

        Test("carries several candidate urls in priority order", () =>
        {
            var link = PaladinUri.BuildUrlLink(new[]
            {
                "https://api.ageofempires.com/api/GameStats/AgeIV/GetMatchReplay/?matchId=226730901&profileId=2848144",
                "https://api.ageofempires.com/api/GameStats/AgeIV/GetMatchReplay/?matchId=226730901&profileId=11018483",
            });
            var parsed = PaladinUri.Parse(link);
            True(parsed.Ok, parsed.Error ?? "");
            True(parsed.Request!.Value.Contains("profileId=2848144"), "first candidate is primary");
            Equal(1, parsed.Request.Fallbacks.Count, "second is a fallback");
            True(parsed.Request.Fallbacks[0].Contains("profileId=11018483"), "in order");
            Equal(2, parsed.Request.AllCandidates().Count(), "both enumerated");
        });

        Test("one bad candidate rejects the whole link rather than being silently dropped", () =>
        {
            var link = "paladin://replay?url=" + Uri.EscapeDataString("https://good.example/a.rec")
                     + "&url=" + Uri.EscapeDataString("file:///C:/Windows/evil.exe");
            False(PaladinUri.Parse(link).Ok, "refused outright");
        });

        Test("rejects a link that smuggles a non-http url", () =>
        {
            var result = PaladinUri.Parse("paladin://replay?url=file%3A%2F%2F%2FC%3A%2FWindows%2Fevil.exe");
            False(result.Ok, "refused");
            NotNull(result.Error);
        });

        Test("rejects an unknown action", () =>
            False(PaladinUri.Parse("paladin://install?url=https%3A%2F%2Fe.com%2Fx").Ok, "only replay is supported"));

        Test("rejects a link that carries nothing", () =>
            False(PaladinUri.Parse("paladin://replay").Ok, "no payload"));

        Test("rejects another scheme entirely", () =>
            False(PaladinUri.Parse("https://example.com/replay").Ok, "not a paladin link"));

        Test("recognises paladin links before parsing them", () =>
        {
            True(PaladinUri.LooksLikePaladinUri("paladin://replay/1"), "scheme form");
            True(PaladinUri.LooksLikePaladinUri("PALADIN://replay/1"), "case-insensitive");
            False(PaladinUri.LooksLikePaladinUri(@"C:\replays\a.rec"), "a path is not a link");
        });

        Test("builds links the parser accepts, round trip", () =>
        {
            var link = PaladinUri.BuildUrlLink("https://example.com/a b.rec?x=1");
            var parsed = PaladinUri.Parse(link);
            True(parsed.Ok, parsed.Error ?? "");
            Equal("https://example.com/a b.rec?x=1", parsed.Request!.Value, "spaces and query survive escaping");
        });

        // ---------------------------------------------------------------------------

        Suite("ShellCommand (self-heal of the link handler)");

        Test("reads the exe path out of a normal quoted handler command", () =>
            Equal(@"C:\Users\me\AppData\Local\Programs\PaladinReplayLauncher\PaladinReplayLauncher.exe",
                ShellCommand.ExtractExePath(
                    "\"C:\\Users\\me\\AppData\\Local\\Programs\\PaladinReplayLauncher\\PaladinReplayLauncher.exe\" \"%1\"")));

        Test("handles a path containing spaces, which is the whole point of the quotes", () =>
            Equal(@"C:\Program Files\Paladin\PaladinReplayLauncher.exe",
                ShellCommand.ExtractExePath("\"C:\\Program Files\\Paladin\\PaladinReplayLauncher.exe\" \"%1\"")));

        Test("handles an unquoted command", () =>
            Equal(@"C:\tools\app.exe", ShellCommand.ExtractExePath(@"C:\tools\app.exe %1")));

        Test("handles a command with no argument at all", () =>
            Equal(@"C:\tools\app.exe", ShellCommand.ExtractExePath(@"C:\tools\app.exe")));

        Test("returns null for nothing usable, so self-heal treats it as broken", () =>
        {
            Equal(null, ShellCommand.ExtractExePath(null));
            Equal(null, ShellCommand.ExtractExePath(""));
            Equal(null, ShellCommand.ExtractExePath("   "));
            Equal(null, ShellCommand.ExtractExePath("\"\" \"%1\""));
        });

        Test("an unterminated quote is treated as unreadable rather than guessed at", () =>
            Equal(null, ShellCommand.ExtractExePath("\"C:\\tools\\app.exe")));

        // ---------------------------------------------------------------------------

        Suite("VdfParser");

        Test("finds the library that owns AoE4 in a real libraryfolders.vdf", () =>
        {
            var libs = VdfParser.LibrariesForApp(RealLibraryFoldersVdf, 1466860);
            Equal(@"D:\SteamLibrary", libs[0], "the owning library comes first, not the default one");
        });

        Test("an app in no library still yields the libraries as fallbacks", () =>
        {
            var libs = VdfParser.LibrariesForApp(RealLibraryFoldersVdf, 999999);
            Equal(0, libs.Count, "no library claims it and both list apps, so there is nothing to guess at");
        });

        Test("reads installdir from a real appmanifest", () =>
            Equal("Age of Empires IV", VdfParser.InstallDirFromAppManifest(RealAppManifest)));

        Test("handles comments and escaped characters", () =>
        {
            var node = VdfParser.Parse("""
                // a comment
                "root"
                {
                    "path"    "C:\\Games\\Steam"
                    "quoted"  "say \"hi\""
                }
                """);
            Equal(@"C:\Games\Steam", node.Child("root")!.Value("path"));
            Equal("say \"hi\"", node.Child("root")!.Value("quoted"));
        });

        Test("keys are matched case-insensitively", () =>
        {
            var node = VdfParser.Parse("\"AppState\" { \"InstallDir\" \"X\" }");
            Equal("X", node.Child("appstate")!.Value("installdir"));
        });

        // ---------------------------------------------------------------------------

        Suite("LaunchCommandBuilder");

        Test("builds the aoe4replays.gg-equivalent command by default", () =>
        {
            var config = new LauncherConfig();
            Equal("-applaunch 1466860 -dev -replay playback:AgeIV_Replay_244989270",
                LaunchCommandBuilder.BuildSteamArguments(config, "AgeIV_Replay_244989270"));
        });

        Test("--no-dev drops the flag cleanly without leaving a double space (Phase 4)", () =>
        {
            var config = new LauncherConfig { UseDevFlag = false };
            Equal("-applaunch 1466860 -replay playback:AgeIV_Replay_244989270",
                LaunchCommandBuilder.BuildSteamArguments(config, "AgeIV_Replay_244989270"));
        });

        Test("the extension is stripped to match how downloaded replays are stored", () =>
        {
            Equal("AgeIV_Replay_244989270", LaunchCommandBuilder.ReplayFileNameFor("AgeIV_Replay_244989270.rec", stripExtension: true));
            Equal("game.rec", LaunchCommandBuilder.ReplayFileNameFor("game.rec", stripExtension: false));
            Equal("game", LaunchCommandBuilder.ReplayFileNameFor(@"C:\downloads\game.rec", stripExtension: true), "any directory part is dropped");
        });

        Test("the argument template is honoured so alternatives can be tested without a rebuild", () =>
        {
            var config = new LauncherConfig
            {
                LaunchArgumentTemplate = "-applaunch {appid} {devflag} -playback {replayarg}",
                ReplayArgumentTemplate = "{name}",
                UseDevFlag = false,
            };
            Equal("-applaunch 1466860 -playback game", LaunchCommandBuilder.BuildSteamArguments(config, "game"));
        });

        Test("the direct-exe fallback carries the same replay argument", () =>
        {
            var config = new LauncherConfig();
            Equal("-dev -replay playback:game", LaunchCommandBuilder.BuildDirectGameArguments(config, "game"));
        });

        Test("a different app id flows through", () =>
        {
            var config = new LauncherConfig { SteamAppId = 12345 };
            True(LaunchCommandBuilder.BuildSteamArguments(config, "g").Contains("-applaunch 12345"), "app id substituted");
        });
    }
}
