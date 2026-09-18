using System.Globalization;
using System.Text;
using Paladin.Core.Config;
using Paladin.Core.Dump;
using Paladin.Core.Protocol;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// "Dump this game" (§717), pass B: the run itself, with the keyboard, the game's log,
/// the disk and Paladin all faked, so every rule of §3.3 and §3.6 is exercised without a
/// game — the two chords, the ladder, the chunk retry, the focus rule, the evidence, the
/// record and every upload answer.
///
/// The scripted game below prints what the real one printed: the markers and the row
/// shapes are the owner's own (the 249960029 session file's forms), and the fatal line is
/// the 244129849 one verbatim but for its padding.
/// </summary>
public static class DumpRunTests
{
    // ---- a game that answers the console -------------------------------------------------

    /// <summary>One line the fake game prints, with the clock prefix the session file uses.</summary>
    private static string Printed(string message) => $"04:53:44.792   {message}";

    /// <summary>
    /// A fake keyboard and a fake log wired together: a pasted line does nothing until
    /// Return, and then the game prints whatever that line would have printed. Focus
    /// losses are scripted by line number, which is how §3.6's rules are tested.
    /// </summary>
    private sealed class ScriptedGame : IConsoleKeys, IDumpLogSource
    {
        private readonly List<string> _lines = new();
        private string? _pending;

        public ScriptedGame(int entityCount, IEnumerable<string>? initialLines = null)
        {
            EntityCount = entityCount;
            if (initialLines is not null) _lines.AddRange(initialLines);
        }

        public int EntityCount { get; }

        /// <summary>The ENV line's count field, so a non-numeric one can be scripted (F3).</summary>
        public string EnvCountField { get; set; } = "";

        /// <summary>Chords that do NOT open the console: HELLO simply never prints for them.</summary>
        public HashSet<ConsoleChord> DeadChords { get; } = new();

        /// <summary>The console stays shut for this many HELLO attempts, then opens.</summary>
        public int HelloAttemptsToIgnore { get; set; }

        /// <summary>Chunks whose PD2 prints nothing at all, by start index.</summary>
        public HashSet<int> SilentChunks { get; } = new();

        /// <summary>What the paste of the nth line (1-based, counting every paste) reports instead of Delivered.</summary>
        public Dictionary<int, KeyDelivery> PasteFailures { get; } = new();

        /// <summary>A line that makes the game print the 244129849 fatal instead of its marker.</summary>
        public string? FatalOnLine { get; set; }

        public List<string> Pasted { get; } = new();
        public List<ConsoleChord> Chords { get; } = new();
        public int HelloAttempts { get; private set; }
        public bool InFront { get; set; } = true;

        public string InputMethod { get; private set; } = "paste";

        public KeySendResult Chord(ConsoleChord chord)
        {
            Chords.Add(chord);
            // A chord the game does not bind does nothing at all — which is the real
            // behaviour of Ctrl+Shift on the owner's machine, where only Alt+Shift opens it.
            if (!DeadChords.Contains(chord)) ConsoleOpen = !ConsoleOpen;
            return KeySendResult.Ok();
        }

        public bool ConsoleOpen { get; private set; }

        public KeySendResult PasteLine(string text)
        {
            var nth = Pasted.Count + 1;
            if (PasteFailures.TryGetValue(nth, out var delivery))
            {
                // A line that was not delivered is not "pending": the game never sees it.
                Pasted.Add(text);
                return delivery switch
                {
                    KeyDelivery.NotSent => KeySendResult.NotSent("scripted: the game was not in front"),
                    KeyDelivery.LostAfter => KeySendResult.LostAfter("scripted: the foreground changed under the keys"),
                    _ => KeySendResult.Failed("scripted: SendInput refused"),
                };
            }
            Pasted.Add(text);
            _pending = text;
            return KeySendResult.Ok();
        }

        public KeySendResult Enter()
        {
            var line = _pending;
            _pending = null;
            if (line is not null) Run(line);
            return KeySendResult.Ok();
        }

        public bool IsGameInFront() => InFront;

        /// <summary>What the game prints for a guarded line it was given.</summary>
        private void Run(string guarded)
        {
            if (!ConsoleOpen) return;

            if (guarded == DumpLadder.Guard(DumpLadder.Hello))
            {
                HelloAttempts++;
                if (HelloAttempts <= HelloAttemptsToIgnore) return;
                _lines.Add(Printed("PALADIN3_HELLO|function: 00007FF712F17B30|function: 0000025FA6270070"));
                _lines.Add(Printed($"PALADIN3_ENV|{(EnvCountField.Length > 0 ? EnvCountField : EntityCount.ToString(CultureInfo.InvariantCulture))}|6.0|true"));
                return;
            }

            if (FatalOnLine is not null && guarded == FatalOnLine)
            {
                _lines.Add(Printed("""GameObj::OnFatalScarError: [[string "__Internal_Game_Quicksaveprint("PALADIN2_LEN|320") --xxxx..."]:1: attempt to call a nil value (global '__Internal_Game_Quicksaveprint')]"""));
                return;
            }

            if (guarded == DumpLadder.Guard(DumpLadder.EntityLadder[^1]))
            {
                _lines.Add(Printed("PALADIN2_DEF|function: 000002603B4F8A20|function: 0000026008FDE120|function: 000002601A834BB0|function: 000002603BB20690|function: 000002600862CCA0|function: 000002600862F1C0|function: 000002600862D900"));
                return;
            }

            if (guarded == DumpLadder.Guard(DumpLadder.Begin))
            {
                _lines.Add(Printed($"PALADIN2_BEGIN|{EntityCount + 5}|32.125"));
                _lines.Add(Printed("PALADIN2_PLAYER|1|May|sultanate|1|32.5|128.5"));
                _lines.Add(Printed("PALADIN2_PLAYER|2|??Creator|ottoman|0|-31.5|-127.5"));
                return;
            }

            if (guarded.StartsWith(DumpLadder.GuardPrefix + "PD2(", StringComparison.Ordinal))
            {
                var inside = guarded[(DumpLadder.GuardPrefix.Length + 4)..].TrimEnd(')');
                var parts = inside.Split(',');
                var a = int.Parse(parts[0], CultureInfo.InvariantCulture);
                var b = int.Parse(parts[1], CultureInfo.InvariantCulture);
                if (SilentChunks.Contains(a)) return;
                for (var i = a; i <= b; i++)
                    _lines.Add(Printed($"PALADIN2|{i}|{1000005002 + i}|-1|spruce_tall_wild|-79.5|17.162261962891|-114.5|world"));
                _lines.Add(Printed($"PALADIN2_DONE|{a}|{b}|{b - a + 1}"));
                return;
            }

            if (guarded == DumpLadder.Guard(DumpLadder.SquadLadder[^1]))
            {
                _lines.Add(Printed("PALADIN3_SQDEF|function: 1|function: 2|function: 3|function: 4|function: 5|function: 6|function: 7"));
                return;
            }

            if (guarded == DumpLadder.Guard(DumpLadder.SquadCall))
            {
                _lines.Add(Printed("PALADIN3_SQ_BEGIN|2"));
                _lines.Add(Printed("PALADIN3_SQ|0|2000000001|villager|1.0|2.0|3.0|1|1000005002|1"));
                _lines.Add(Printed("PALADIN3_SQ_DONE|2"));
            }
        }

        /// <summary>The game writing a line of its own, unasked (the account prompt, a fatal).</summary>
        public void Print(string rawLine) => _lines.Add(rawLine);

        // ---- IDumpLogSource ---------------------------------------------------------------

        public Task<bool> WaitForSessionLogAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(SessionLogPath is not null);

        public string? SessionLogPath { get; set; } = @"LogFiles\AoE4_09_18_04h-53m-19s\warnings.2026-09-18.04-53-19.txt";

        public string LogFilesDisplayPath { get; set; } = @"~\...\Age of Empires IV\LogFiles";

        public IReadOnlyList<string> Lines => _lines;

        public string TextSoFar => string.Join("\n", _lines);

        public int Refresh() => _lines.Count;

        /// <summary>The scripted game answers at once or not at all: a timeout costs the tests no wall clock.</summary>
        public Task<int> WaitForLineAsync(int fromIndex, Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct)
        {
            for (var i = Math.Max(0, fromIndex); i < _lines.Count; i++)
                if (predicate(_lines[i]))
                    return Task.FromResult(i);
            return Task.FromResult(-1);
        }

        public string? TopLevelHead { get; set; } = string.Join("\n", new[]
        {
            "RelicCardinal started at 2026-09-18 04:53 [GMT Summer Time UTC 00:00]",
            "RUN-OPTIONS [-dev -replay playback:AgeIV_Replay_249960029]",
            "(I) [04:53:19.236] [000030660]: Version [16.3.11308.0] Info [[Cardinal][cardinal][release_16_3_0][rtm][4178464]]",
        });

        public string? ReadTopLevelHead() => TopLevelHead;
    }

    /// <summary>The five MapGen lines and the mission line the run needs before the console opens.</summary>
    private static IEnumerable<string> BeforeTheConsole() => new[]
    {
        "04:53:33.633   MapGen -- Generating with biome taiga_woodlands_snow",
        "04:53:33.633   MapGen -- Generating with layout highview",
        "04:53:33.633   MapGen -- Generating with size map_size_416",
        "04:53:33.633   MapGen -- Generating with seed 1176837288 (0x46251CA8)",
        "04:53:33.633   MapGen -- Generating with player count 2",
        "04:53:38.737   GAME -- Starting mission: ",
    };

    private sealed class FakeReporter : IDumpReporter
    {
        public List<string> All { get; } = new();
        public List<string> Oks { get; } = new();
        public List<string> Warns { get; } = new();
        public List<string> Fails { get; } = new();

        public void Ok(string message) { All.Add(message); Oks.Add(message); }
        public void Pending(string message) => All.Add(message);
        public void Warn(string message) { All.Add(message); Warns.Add(message); }
        public void Fail(string message) { All.Add(message); Fails.Add(message); }
        public void Note(string message) => All.Add(message);

        public bool Said(string fragment) => All.Any(m => m.Contains(fragment, StringComparison.Ordinal));
    }

    private sealed class FakeStore : IDumpStore
    {
        /// <summary>Every write in order: "record:&lt;phase&gt;", "evidence", "envelope".</summary>
        public List<string> Writes { get; } = new();
        public string? Evidence { get; private set; }
        public DumpEnvelope? Envelope { get; private set; }
        public DumpRecord? Last { get; private set; }

        public string DisplayFolder => @"~\...\Sessions\20260918-045319-abc123\dump";

        public void SaveRecord(DumpRecord record)
        {
            Last = DumpRecord.FromJson(record.ToJson());   // a copy, as a real temp+replace write is
            Writes.Add($"record:{record.Phase}");
        }

        public string SaveEvidence(string text)
        {
            Evidence = text;
            Writes.Add("evidence");
            return @"C:\sessions\dump\evidence.txt";
        }

        public string SaveEnvelope(DumpEnvelope envelope)
        {
            Envelope = envelope;
            Writes.Add("envelope");
            return @"C:\sessions\dump\envelope.json";
        }
    }

    private sealed class FakeUploader : IDumpUploadService
    {
        private readonly DumpUploadResult _answer;
        public FakeUploader(DumpUploadResult answer) => _answer = answer;

        public int Uploads { get; private set; }
        public DumpEnvelope? Sent { get; private set; }
        public DumpPreflight Preflight { get; set; } = new(true, new DumpStatus { Status = "none" }, 200, null);

        /// <summary>Where this uploader sends: a test points it at the wrangler dev target to watch the console follow.</summary>
        public string Host { get; set; } = DumpConsoleText.DefaultHost;

        public Task<DumpPreflight> CheckAsync(long gameId, CancellationToken ct) => Task.FromResult(Preflight);

        public Task<DumpUploadResult> UploadAsync(DumpEnvelope envelope, CancellationToken ct)
        {
            Uploads++;
            Sent = envelope;
            return Task.FromResult(_answer);
        }
    }

    private static DumpUploadResult Stored() =>
        DumpUploader.Classify(200, "OK", """{"status":"stored","gameId":249960029,"n":5,"verified":["seed","tc","orders"],"url":"/world/249960029.json"}""");

    private static DumpSession Session(
        ScriptedGame game, FakeReporter ui, FakeStore store, IDumpUploadService? uploader,
        bool squads = false, bool upload = true, int chunkSize = 2, bool endProcess = true)
    {
        var pacing = new DumpPacing
        {
            ChordSettleMs = 0, AfterPasteMs = 0, AfterEnterMs = 0, HudSettleMs = 0,
            ChunkSize = chunkSize, CloseGraceSeconds = 15, EndProcess = endProcess,
        };
        return new DumpSession(
            new DumpSessionOptions(
                249960029, "20260918-045319-abc123", "0.4.0-test",
                new DateTime(2026, 9, 18, 3, 53, 19, DateTimeKind.Utc), pacing, squads, upload, 11308),
            game, game, ui, store, uploader);
    }

    public static void Register()
    {
        Suite("ConsoleDriver (the console script)");

        Test("the happy path: the chord, the ladder, BEGIN and every chunk, and quit() is NEVER typed", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var store = new FakeStore();
            var result = Session(game, ui, store, new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            True(result.Ok, result.Failure?.ToString() ?? "");
            Equal("altshift", result.Record.Chord);
            Equal(5, result.Record.EnvCount ?? 0);
            Equal(3, result.Record.Chunks.Count, "5 entities in chunks of 2");
            True(result.Record.Chunks.All(c => c.Done), "every chunk printed its DONE");

            // The contract of DumpLadder.Script, minus its final quit(): D1 amended.
            var expected = DumpLadder.Script(5, includeSquads: false, chunkSize: 2)
                .Select(l => l.Text).ToList();
            Equal(DumpLadder.Guard(DumpLadder.Quit), expected[^1], "Script still ends with quit()");
            expected.RemoveAt(expected.Count - 1);
            Equal(string.Join("\n", expected), string.Join("\n", game.Pasted.Skip(1)), "the lines after HELLO are Script's, in order");
            False(game.Pasted.Any(line => line.Contains("quit()", StringComparison.Ordinal)), "quit() is never typed");
        });

        Test("the happy path prints the §2.2 lines: the console, the chunks, the rows and Paladin's answer", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var result = Session(game, ui, new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            True(result.Ok, "the run worked");
            True(ui.Said("Replay started"), "the replay started");
            True(ui.Said("Console open — 5 objects on the map"), "the console line names the count");
            True(ui.Said("Printing the map: chunk 2 of 3 ..."), "the chunk progress");
            True(ui.Said("5 of 5 objects printed, 0 errors"), "the row count");
            True(ui.Said("Paladin accepted the map: seed, town centres, worked targets match its own data."), "the Worker's verified list in plain words");
            True(ui.Said("Reload the match page to see the World layer."), "the reload note");
        });

        Test("HELLO that fails on the first chord lands on the second try of the same chord", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole()) { HelloAttemptsToIgnore = 1 };
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            True(result.Ok, result.Failure?.ToString() ?? "");
            Equal("altshift", result.Record.Chord);
            // chord, toggle back, chord: three chord sends before the console answered.
            Equal(3, game.Chords.Count, "the console was toggled back between the tries");
            Equal(2, game.HelloAttempts);
        });

        Test("HELLO that never lands on either chord is F2, and both chords were tried twice", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole()) { HelloAttemptsToIgnore = 99 };
            game.DeadChords.Add(ConsoleChord.CtrlShift);   // as on the owner's machine
            var ui = new FakeReporter();
            var result = Session(game, ui, new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F2", result.Failure?.Code);
            Equal(DumpExitCodes.ConsoleNeverOpened, result.Failure!.ExitCode);
            True(result.Failure.Text.StartsWith("The game's developer console did not open", StringComparison.Ordinal), result.Failure.Text);
            Equal(4, game.HelloAttempts, "two tries on each of the two chords");
            // Each chord: try, toggle back, try — three sends, six in all.
            Equal(3, game.Chords.Count(c => c == ConsoleChord.AltShift));
            Equal(3, game.Chords.Count(c => c == ConsoleChord.CtrlShift));
            True(ui.Fails.Any(f => f.Contains("Alt+Shift+` and Ctrl+Shift+` were tried", StringComparison.Ordinal)), string.Join(" | ", ui.Fails));
        });

        Test("an ENV count that is not a number is F3: the game's sign-in has lapsed", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole()) { EnvCountField = "nil" };
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F3", result.Failure?.Code);
            Equal(DumpExitCodes.NotSignedIn, result.Failure!.ExitCode);
            True(result.Failure.Text.Contains("not signed in", StringComparison.Ordinal), result.Failure.Text);
            Equal(1, game.Pasted.Count, "nothing was typed after the ENV line was read");
        });

        Test("a fatal Scar error on a ladder line is F6 and stops at once", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            game.FatalOnLine = DumpLadder.Guard(DumpLadder.EntityLadder[^1]);
            var ui = new FakeReporter();
            var store = new FakeStore();
            var uploader = new FakeUploader(Stored());
            var result = Session(game, ui, store, uploader).RunAsync(CancellationToken.None).Result;

            Equal("F6", result.Failure?.Code);
            Equal(DumpExitCodes.FatalScarError, result.Failure!.ExitCode);
            True(result.Record.Fatal, "the record carries the fatal");
            Equal(0, uploader.Uploads, "nothing was sent");
            Equal(11, game.Pasted.Count - 1, "the ladder's eleven lines went in and nothing after them");
        });

        Test("a fatal on a line that prints nothing is still F6, not a dropped definition", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            game.FatalOnLine = DumpLadder.Guard(DumpLadder.EntityLadder[2]);   // a line with no marker of its own
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F6", result.Failure?.Code);
            Equal(4, game.Pasted.Count, "HELLO and three ladder lines: nothing was sent after the fatal");
        });

        Test("a chunk whose DONE never comes is sent once more — and a focus loss on that retry is F7, with nothing re-sent", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            game.SilentChunks.Add(2);                    // the second chunk prints nothing
            var ui = new FakeReporter();
            var store = new FakeStore();
            var uploader = new FakeUploader(Stored());

            // Lines: 1 HELLO, 2-12 ladder, 13 BEGIN, 14 chunk(0,1), 15 chunk(2,3), 16 its retry.
            game.PasteFailures[16] = KeyDelivery.LostAfter;
            var result = Session(game, ui, store, uploader).RunAsync(CancellationToken.None).Result;

            Equal("F7", result.Failure?.Code);
            Equal(DumpExitCodes.FocusLost, result.Failure!.ExitCode);
            True(result.Failure.Text.Contains("rather than retype a half-typed line", StringComparison.Ordinal), result.Failure.Text);
            Equal(16, game.Pasted.Count, "the lost line was the last thing sent: no third try, no line after it");
            Equal(2, result.Record.Chunks[^1].Tries, "the silent chunk was tried twice, as §3.6 rule 4 allows");
            Equal(0, uploader.Uploads, "nothing was sent to Paladin");
            True(store.Writes.Contains("evidence"), "what had been printed was still kept");
        });

        Test("a line the keyboard would not send at all is F7 too, and the game is left alone", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            game.PasteFailures[3] = KeyDelivery.NotSent;    // the second ladder line
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F7", result.Failure?.Code);
            Equal(3, game.Pasted.Count, "nothing was sent after the refusal");
        });

        Test("the account prompt in the log does not stop a hands-off run", () =>
        {
            var lines = BeforeTheConsole().ToList();
            lines.Insert(0, @"04:53:30.616   UI Async loading data:ui\shared\modalpages\UserPromptModalPage.xaml");
            var game = new ScriptedGame(5, lines);
            game.Print(@"04:53:38.793   UI Async loading data:ui\shared\modalpages\UserPromptModalPage.xaml");

            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;
            True(result.Ok, result.Failure?.ToString() ?? "");
        });

        Test("no \"Starting mission\" is F1: the map's mod is missing, and nothing was typed", () =>
        {
            var game = new ScriptedGame(5);   // no mission line ever
            var ui = new FakeReporter();
            var result = Session(game, ui, new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F1", result.Failure?.Code);
            Equal(DumpExitCodes.ReplayNeverStarted, result.Failure!.ExitCode);
            True(result.Failure.Text.Contains("Tournament games on custom maps", StringComparison.Ordinal), result.Failure.Text);
            Equal(0, game.Pasted.Count, "the console was never touched");
        });

        Test("a game whose log never appears is NOT F1: it names the folder Paladin watched, not a missing map mod", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole()) { SessionLogPath = null };
            var ui = new FakeReporter();
            var result = Session(game, ui, new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal("F14", result.Failure?.Code);
            True(result.Failure!.Text.Contains(@"~\...\Age of Empires IV\LogFiles", StringComparison.Ordinal), result.Failure.Text);
            False(result.Failure.Text.Contains("Tournament games on custom maps", StringComparison.Ordinal),
                "a redirected Documents folder is not a missing map mod");
            False(result.Failure.Text.Contains("within 3 minutes", StringComparison.Ordinal), "and no wait that never happened");
            Equal(DumpExitCodes.ReplayNeverStarted, result.Failure.ExitCode, "one code for \"this game cannot be dumped here\"");
            Equal(0, game.Pasted.Count, "the console was never touched");
        });

        Test("--squads types the squad ladder and its call; without it neither is typed", () =>
        {
            var withSquads = new ScriptedGame(5, BeforeTheConsole());
            var result = Session(withSquads, new FakeReporter(), new FakeStore(), new FakeUploader(Stored()), squads: true)
                .RunAsync(CancellationToken.None).Result;
            True(result.Ok, result.Failure?.ToString() ?? "");
            True(withSquads.Pasted.Contains(DumpLadder.Guard(DumpLadder.SquadCall)), "the squad call went in");
            Equal(
                string.Join("\n", DumpLadder.Script(5, includeSquads: true, chunkSize: 2).Select(l => l.Text).SkipLast(1)),
                string.Join("\n", withSquads.Pasted.Skip(1)),
                "with --squads the lines are Script(includeSquads: true) without quit()");

            var without = new ScriptedGame(5, BeforeTheConsole());
            Session(without, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Wait();
            False(without.Pasted.Any(l => l.Contains("SGroup_Create", StringComparison.Ordinal)), "no squad ladder without --squads");
        });

        // ---------------------------------------------------------------------------

        Suite("DumpSession (the run's decisions)");

        Test("the evidence and the record are on disk BEFORE the result asks for the game to be ended", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var store = new FakeStore();
            var result = Session(game, new FakeReporter(), store, new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            Equal(ExitAction.EndProcess, result.Exit.Action);
            Equal(15, (int)result.Exit.Grace.TotalSeconds);
            var evidenceAt = store.Writes.IndexOf("evidence");
            True(evidenceAt >= 0, "the evidence was written");
            True(store.Writes.IndexOf("envelope") > evidenceAt, "the envelope after it");
            True(store.Writes.Last().StartsWith("record:", StringComparison.Ordinal), "the record is written last, with the outcome");
            Equal("done", store.Last!.Phase);
        });

        Test("the record follows the run through its phases and keeps the chunks and the timings", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var store = new FakeStore();
            var result = Session(game, new FakeReporter(), store, new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            var phases = store.Writes.Where(w => w.StartsWith("record:", StringComparison.Ordinal)).Select(w => w[7..]).ToList();
            True(phases.Contains("launched") && phases.Contains("mission") && phases.Contains("console"), string.Join(",", phases));
            True(phases.Contains("uploading") && phases[^1] == "done", string.Join(",", phases));
            Equal("paste", result.Record.InputMethod);
            Equal(3, store.Last!.Chunks.Count);
            NotNull(store.Last.Timings.Hello, "the HELLO time");
            NotNull(store.Last.Timings.LastDone, "the last DONE time");
            Equal(DumpUploadStatus.Done, store.Last.Upload.Status);
        });

        Test("the envelope carries the rows, the header and the counts the Worker checks", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var store = new FakeStore();
            var uploader = new FakeUploader(Stored());
            Session(game, new FakeReporter(), store, uploader).RunAsync(CancellationToken.None).Wait();

            var envelope = uploader.Sent!;
            Equal(249960029, envelope.GameId);
            Equal(1, envelope.V);
            Equal(11308, envelope.ReplayBuild ?? 0);
            Equal("altshift", envelope.Chord);
            Equal("paste", envelope.Input);
            Equal(5, envelope.Counts.Env);
            Equal(5, envelope.Counts.Rows);
            Equal(2, envelope.Counts.Players);
            Equal(3, envelope.Counts.Chunks);
            Equal(8, envelope.Header.Split('\n').Length, "three top-level lines and five MapGen lines");
            True(envelope.Header.Contains("RUN-OPTIONS [-dev -replay playback:AgeIV_Replay_249960029]", StringComparison.Ordinal), envelope.Header);
            True(envelope.Rows.StartsWith("04:53:44.792   PALADIN3_HELLO|", StringComparison.Ordinal), "the rows keep their clock prefix");
            False(envelope.Rows.Contains("MapGen", StringComparison.Ordinal), "the MapGen lines are header, not rows");
            Equal(envelope.Rows, store.Evidence![(envelope.Header.Length + 1)..], "the kept file is header + newline + rows");
        });

        Test("fewer rows than the map has objects is F5, with the counts and the folder in the sentence, and nothing is sent", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            game.SilentChunks.Add(4);        // the last chunk never prints
            var store = new FakeStore();
            var uploader = new FakeUploader(Stored());
            var result = Session(game, new FakeReporter(), store, uploader).RunAsync(CancellationToken.None).Result;

            Equal("F5", result.Failure?.Code);
            Equal(DumpExitCodes.Incomplete, result.Failure!.ExitCode);
            Equal("Printed 4 of 5 objects; Paladin needs all of them. Nothing was sent. The rows are kept at ~\\...\\Sessions\\20260918-045319-abc123\\dump. Try again.", result.Failure.Text);
            Equal(0, uploader.Uploads);
            True(store.Writes.Contains("evidence"), "the rows are kept");
            False(store.Writes.Contains("envelope"), "no envelope is built for rows that cannot be sent");
        });

        Test("--no-upload keeps the rows and asks Paladin nothing", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var store = new FakeStore();
            var uploader = new FakeUploader(Stored());
            var result = Session(game, ui, store, uploader, upload: false).RunAsync(CancellationToken.None).Result;

            True(result.Ok, result.Failure?.ToString() ?? "");
            Equal(0, uploader.Uploads);
            Equal(DumpUploadStatus.Skipped, result.Record.Upload.Status);
            True(store.Writes.Contains("envelope"), "the envelope is still kept, so --dump-upload can send it later");
            True(ui.Said("kept at ~\\...\\Sessions\\20260918-045319-abc123\\dump (nothing was sent)"), string.Join(" | ", ui.All));
        });

        Test("a 400 from the Worker is F10 in plain words", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var rejected = DumpUploader.Classify(400, "Bad Request", """{"error":"seed_mismatch","detail":"the map seed is not this game's"}""");
            var result = Session(game, ui, new FakeStore(), new FakeUploader(rejected)).RunAsync(CancellationToken.None).Result;

            Equal("F10", result.Failure?.Code);
            Equal(DumpExitCodes.UploadRejected, result.Failure!.ExitCode);
            Equal("Paladin did not accept the map: the replay that played is not this game.", result.Failure.Text);
            Equal(DumpUploadStatus.Rejected, result.Record.Upload.Status);
        });

        Test("an unreachable Paladin is F11: a warning, the rows kept, and the exact command to send them later", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var unreachable = new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, "no answer within 60 s");
            var result = Session(game, ui, new FakeStore(), new FakeUploader(unreachable)).RunAsync(CancellationToken.None).Result;

            True(result.Ok, "an outage is not a failed dump");
            Equal(DumpUploadStatus.Pending, result.Record.Upload.Status);
            Equal(1, ui.Warns.Count);
            True(ui.Warns[0].Contains("--dump-upload 20260918-045319-abc123", StringComparison.Ordinal), ui.Warns[0]);
        });

        Test("\"already\" means someone else was first: not a failure, and nothing more is sent", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var already = DumpUploader.Classify(200, "OK", """{"status":"already","src":"user","n":1310}""");
            var result = Session(game, ui, new FakeStore(), new FakeUploader(already)).RunAsync(CancellationToken.None).Result;

            True(result.Ok, "the run did its job");
            True(ui.Said("Someone else's map for this game arrived first"), string.Join(" | ", ui.All));
            Equal(DumpUploadStatus.Rejected, result.Record.Upload.Status);
        });

        Test("attended mode leaves the game to the user instead of ending it", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored()), endProcess: false)
                .RunAsync(CancellationToken.None).Result;

            Equal(ExitAction.WaitForUser, result.Exit.Action);
        });

        Test("Ctrl+C mid-run leaves a record that says so, and the cancellation reaches the launcher", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var store = new FakeStore();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // The cancellation is the launcher's to handle — it ends the game and restores —
            // so it must come out of here, not be swallowed into a result.
            Throws<OperationCanceledException>(
                () => { try { Session(game, new FakeReporter(), store, new FakeUploader(Stored())).RunAsync(cts.Token).Wait(); } catch (AggregateException ex) { throw ex.GetBaseException(); } },
                "the launcher sees the Ctrl+C");

            Equal("failed", store.Last!.Phase, "the record does not sit at the phase it was interrupted in");
            True(store.Last.Failure!.StartsWith("interrupted", StringComparison.Ordinal), store.Last.Failure);
        });

        Test("a failed run still ends with an exit policy, so the Shield always gets its turn", () =>
        {
            var game = new ScriptedGame(5);   // never starts
            var result = Session(game, new FakeReporter(), new FakeStore(), new FakeUploader(Stored())).RunAsync(CancellationToken.None).Result;

            False(result.Ok, "it failed");
            Equal(ExitAction.EndProcess, result.Exit.Action);
            Equal("failed", result.Record.Phase);
            NotNull(result.Record.Failure, "the record says why");
        });

        // ---------------------------------------------------------------------------

        Suite("The pre-flight and the countdown");

        Test("status none goes ahead and says the game has no world layer yet", () =>
        {
            var decision = DumpPreflightDecision.For(new DumpPreflight(true, new DumpStatus { Status = "none" }, 200, null), force: false);
            True(decision.Proceed, "it goes ahead");
            Equal("Paladin has no world layer for this game yet", decision.Message);
            False(decision.Warn, "an ordinary [ok] line");
        });

        Test("status owner stops before anything is launched, and --force overrides it", () =>
        {
            var owner = new DumpPreflight(true, new DumpStatus { Status = "owner" }, 200, null);
            var stop = DumpPreflightDecision.For(owner, force: false);
            False(stop.Proceed, "it stops");
            True(stop.Message.Contains("the owner's map", StringComparison.Ordinal), stop.Message);
            Equal(DumpExitCodes.UploadRejected, stop.Failure!.ExitCode);

            var forced = DumpPreflightDecision.For(owner, force: true);
            True(forced.Proceed, "--force dumps it anyway");
            True(forced.Warn, "and says so as a warning");
        });

        Test("status user stops the same way", () =>
        {
            var decision = DumpPreflightDecision.For(new DumpPreflight(true, new DumpStatus { Status = "user" }, 200, null), force: false);
            False(decision.Proceed, "it stops");
            True(decision.Message.Contains("a map someone sent", StringComparison.Ordinal), decision.Message);
        });

        Test("status unknown — no sidecar — stops the run before the game is launched", () =>
        {
            var decision = DumpPreflightDecision.For(new DumpPreflight(true, new DumpStatus { Status = "unknown" }, 200, null), force: false);
            False(decision.Proceed, "nothing is launched");
            True(decision.Message.Contains("no map data for this game yet", StringComparison.Ordinal), decision.Message);
            Equal(DumpExitCodes.UploadRejected, decision.Failure!.ExitCode);

            var stillStops = DumpPreflightDecision.For(new DumpPreflight(true, new DumpStatus { Status = "unknown" }, 200, null), force: true);
            False(stillStops.Proceed, "--force cannot conjure a sidecar");
        });

        Test("a status this version does not know stops rather than guesses", () =>
        {
            var decision = DumpPreflightDecision.For(new DumpPreflight(true, new DumpStatus { Status = "quarantined" }, 200, null), force: false);
            False(decision.Proceed, "it stops");
            True(decision.Message.Contains("'quarantined'", StringComparison.Ordinal), decision.Message);
        });

        Test("a pre-flight that could not be answered is a warning, and the run goes on", () =>
        {
            var decision = DumpPreflightDecision.For(new DumpPreflight(false, null, null, "no answer within 15 s"), force: false);
            True(decision.Proceed, "an outage does not stop a dump");
            True(decision.Warn, "but it is a warning");
            True(decision.Message.Contains("no answer within 15 s", StringComparison.Ordinal), decision.Message);
        });

        Test("--yes skips the countdown, and so does a console with no keyboard", () =>
        {
            True(DumpCountdown.ShouldWait(assumeYes: false, inputRedirected: false), "a person gets the 5 s");
            False(DumpCountdown.ShouldWait(assumeYes: true, inputRedirected: false), "--yes does not");
            False(DumpCountdown.ShouldWait(assumeYes: false, inputRedirected: true), "a scripted run does not hang");
            Equal(5, DumpCountdown.Seconds);
            Equal("Starting in 5 s — Enter to start now, Ctrl+C to stop.", DumpConsoleText.Countdown(DumpCountdown.Seconds));
        });

        Test("the console says what will be sent BEFORE the countdown, and says it differently with --no-upload", () =>
        {
            var sending = string.Join(" ", DumpConsoleText.WhatWillHappen(upload: true, DumpConsoleText.DefaultHost));
            True(sending.Contains("about 150 KB of text, nothing else", StringComparison.Ordinal), sending);
            True(sending.Contains("paladin.odinmaycall.com", StringComparison.Ordinal), sending);

            var local = string.Join(" ", DumpConsoleText.WhatWillHappen(upload: false, DumpConsoleText.DefaultHost));
            True(local.Contains("--no-upload: nothing is sent", StringComparison.Ordinal), local);
            False(local.Contains("sends those rows", StringComparison.Ordinal), local);
        });

        Test("every line that names an address names the one the rows really go to", () =>
        {
            // §5.5 runs the Worker at localhost:8787; a run pointed there must not tell the
            // user their rows are going to production.
            const string dev = "localhost:8787";
            var consent = string.Join(" ", DumpConsoleText.WhatWillHappen(upload: true, dev));
            True(consent.Contains(dev, StringComparison.Ordinal), consent);
            False(consent.Contains("paladin.odinmaycall.com", StringComparison.Ordinal), consent);

            Equal("Sending 146 KB to localhost:8787 ...", DumpConsoleText.Sending(150_000, dev));
            True(DumpConsoleText.SendLaterWarn("20260918-045319-abc123", dev)
                .StartsWith("localhost:8787 could not be reached.", StringComparison.Ordinal),
                DumpConsoleText.SendLaterWarn("20260918-045319-abc123", dev));
        });

        Test("the host the console names comes from the configured base URL, port and all", () =>
        {
            var log = new Paladin.Core.Logging.PaladinLog();
            Equal("paladin.odinmaycall.com", new DumpUploader(DumpUploader.DefaultBaseUrl, "0.4.0", log).Host);
            Equal("localhost:8787", new DumpUploader("http://localhost:8787/api/world/", "0.4.0", log).Host);
            Equal(DumpConsoleText.DefaultHost, new DumpUploader(DumpUploader.DefaultBaseUrl, "0.4.0", log).Host,
                "the default target is the one the README's privacy paragraph names");
        });

        Test("a run that sends nothing still names the configured host, not a constant", () =>
        {
            var game = new ScriptedGame(5, BeforeTheConsole());
            var ui = new FakeReporter();
            var unreachable = new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, "no answer within 60 s");
            var uploader = new FakeUploader(unreachable) { Host = "localhost:8787" };
            Session(game, ui, new FakeStore(), uploader).RunAsync(CancellationToken.None).Wait();

            True(ui.Said("Sending "), string.Join(" | ", ui.All));
            True(ui.Warns[0].StartsWith("localhost:8787 could not be reached.", StringComparison.Ordinal), ui.Warns[0]);
            False(ui.Said("paladin.odinmaycall.com"), string.Join(" | ", ui.All));
        });

        Test("the three clipboard sentences each say what became of the clipboard", () =>
        {
            // The promise in the README is that the clipboard is never quietly left
            // holding a console line, so every outcome has words of its own.
            var lines = new[]
            {
                DumpConsoleText.ClipboardRestored,
                DumpConsoleText.ClipboardImageLost,
                DumpConsoleText.ClipboardCleared,
                DumpConsoleText.ClipboardNotPutBack,
            };
            Equal(4, lines.Distinct(StringComparer.Ordinal).Count(), "each outcome has its own sentence");
            foreach (var line in lines)
                True(line.Contains("clipboard", StringComparison.OrdinalIgnoreCase), line);
            True(DumpConsoleText.ClipboardCleared.Contains("emptied", StringComparison.Ordinal), DumpConsoleText.ClipboardCleared);
            True(DumpConsoleText.ClipboardNotPutBack.Contains("may still be on it", StringComparison.Ordinal), DumpConsoleText.ClipboardNotPutBack);
        });

        Test("the hands-off note names the two minutes and the account prompt", () =>
        {
            var lines = DumpConsoleText.HandsOff();
            True(lines[0].Contains("Hands off for about two minutes", StringComparison.Ordinal), lines[0]);
            True(lines[1].Contains("Account Authentication", StringComparison.Ordinal), lines[1]);
        });

        // ---------------------------------------------------------------------------

        Suite("GameLogFiles (finding and tailing the game's log)");

        Test("the session folder is the newest one this launch made, never an older one", () =>
        {
            var launch = new DateTime(2026, 9, 18, 4, 53, 19, DateTimeKind.Utc);
            var folders = new[]
            {
                new GameLogFolder("AoE4_09_17_22h-48m-31s", @"C:\l\a", launch.AddDays(-1)),
                new GameLogFolder("AoE4_09_18_04h-53m-19s", @"C:\l\b", launch.AddSeconds(4)),
                new GameLogFolder("AoE4_09_18_04h-59m-02s", @"C:\l\c", launch.AddMinutes(6)),
            };
            var before = new HashSet<string> { "AoE4_09_17_22h-48m-31s" };

            var chosen = GameLogFiles.NewestSessionFolder(folders, before, launch);
            NotNull(chosen, "a folder was chosen");
            Equal("AoE4_09_18_04h-59m-02s", chosen!.Value.Name);

            // A folder that existed before the launch is never chosen, however new it looks.
            var onlyOld = GameLogFiles.NewestSessionFolder(
                folders, new HashSet<string> { "AoE4_09_18_04h-53m-19s", "AoE4_09_18_04h-59m-02s" }, launch);
            Equal(null, onlyOld?.Name);

            // Nor is one created before the launch, even if its name is new to us.
            var stale = GameLogFiles.NewestSessionFolder(
                new[] { new GameLogFolder("AoE4_09_18_04h-40m-00s", @"C:\l\d", launch.AddMinutes(-13)) },
                new HashSet<string>(), launch);
            Equal(null, stale?.Name);
        });

        Test("the folders and the session log are found on disk, newest file first", () =>
        {
            using var temp = new TempDir("dump-logfiles");
            var logFiles = Path.Combine(temp.Path, "LogFiles");
            Directory.CreateDirectory(Path.Combine(logFiles, "AoE4_old"));
            var before = GameLogFiles.ListFolders(logFiles).Select(f => f.Name).ToHashSet();
            Equal(1, before.Count);

            var launch = DateTime.UtcNow;
            var session = Path.Combine(logFiles, "AoE4_new");
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "warnings.2026-09-18.04-53-19.txt"), "first\n");
            File.WriteAllText(Path.Combine(session, "notes.txt"), "not a log\n");

            var chosen = GameLogFiles.NewestSessionFolder(GameLogFiles.ListFolders(logFiles), before, launch);
            NotNull(chosen, "the new folder");
            Equal("AoE4_new", chosen!.Value.Name);

            var log = GameLogFiles.NewestSessionLog(chosen.Value.FullPath);
            NotNull(log, "the warnings file");
            Equal("warnings.2026-09-18.04-53-19.txt", Path.GetFileName(log!));
        });

        Test("the tail reads a file the game still holds open, and hands on no half-written line", () =>
        {
            using var temp = new TempDir("dump-tail");
            var path = Path.Combine(temp.Path, "warnings.2026-09-18.04-53-19.txt");

            // Opened the way the game holds it: written by one handle while the tail reads.
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            void Write(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
            }

            var tail = new LogTail(path);
            Write("04:53:38.737   GAME -- Starting mission: \r\n");
            Equal(1, tail.Refresh());
            Equal("04:53:38.737   GAME -- Starting mission: ", tail.Lines[0]);

            // Half a line is not a line.
            Write("04:53:44.792   PALADIN3_HE");
            Equal(1, tail.Refresh());
            Write("LLO|function: 1|function: 2\n");
            Equal(2, tail.Refresh());
            Equal("PALADIN3_HELLO", DumpLogText.Marker(tail.Lines[1]));

            // A player name split across two reads survives as one character.
            Write("04:54:10.919   PALADIN2_PLAYER|2|Créateur|ottoman|0|-31.5|-127.5\n");
            Equal(3, tail.Refresh());
            True(DumpLogText.TryPlayer(tail.Lines[2], out var player), "the player row parses");
            Equal("Créateur", player.Name);

            // Nothing new means nothing re-read.
            Equal(3, tail.Refresh());
            Equal(3, tail.Lines.Count);
        });

        Test("the top-level log is read to its twelfth line and no further", () =>
        {
            using var temp = new TempDir("dump-toplevel");
            var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
            lines[16] = "GAME -- Current Steam name is [someone]";
            var path = temp.File("warnings.log", string.Join("\n", lines));

            var head = GameLogFiles.ReadHead(path);
            NotNull(head, "the head was read");
            Equal(DumpEvidence.TopLevelLinesRead, head!.Split('\n').Length);
            False(head.Contains("Steam name", StringComparison.Ordinal), "line 17 is past the cut");
        });

        // ---------------------------------------------------------------------------

        Suite("The dump link's then=watch");

        Test("then=watch is accepted on a dump link and is off by default", () =>
        {
            var plain = PaladinUri.Parse("paladin://dump?game=249960029&url=https%3A%2F%2Fexample.com%2Fa.rec");
            True(plain.Ok, plain.Error ?? "");
            False(plain.ThenWatch, "no then= means no watch");

            var watch = PaladinUri.Parse("paladin://dump?game=249960029&url=https%3A%2F%2Fexample.com%2Fa.rec&then=watch");
            True(watch.Ok, watch.Error ?? "");
            True(watch.IsDump, "still a dump");
            Equal(249960029L, watch.GameId ?? 0);
            True(watch.ThenWatch, "and it watches afterwards");
        });

        Test("an unknown then= is ignored, not refused, and a replay link never carries one", () =>
        {
            var odd = PaladinUri.Parse("paladin://dump?game=249960029&url=https%3A%2F%2Fexample.com%2Fa.rec&then=something");
            True(odd.Ok, odd.Error ?? "");
            False(odd.ThenWatch, "an unknown follow-on is simply not done");

            var replay = PaladinUri.Parse("paladin://replay?url=https%3A%2F%2Fexample.com%2Fa.rec&then=watch");
            True(replay.Ok, replay.Error ?? "");
            False(replay.ThenWatch, "then= belongs to a dump link");
        });

        Test("BuildDumpLink round-trips then=watch", () =>
        {
            var link = PaladinUri.BuildDumpLink(249960029, new[] { "https://example.com/a.rec" }, thenWatch: true);
            True(link.EndsWith("&then=watch", StringComparison.Ordinal), link);
            True(PaladinUri.Parse(link).ThenWatch, "and it parses back");
        });

        Test("the replay a dump plays must be this game's", () =>
        {
            True(DumpChecks.NameMatchesGame("AgeIV_Replay_249960029", 249960029), "the plain name");
            True(DumpChecks.NameMatchesGame("AgeIV_Replay_249960029_paladin", 249960029), "a clash suffix still passes, as run.mjs allows");
            False(DumpChecks.NameMatchesGame("AgeIV_Replay_246737201", 249960029), "another game's replay does not");
            Equal(
                "The replay arrived as 'AgeIV_Replay_246737201' but the link named game 249960029. Nothing was launched.",
                DumpChecks.WrongReplayMessage("AgeIV_Replay_246737201", 249960029));
        });

        // ---------------------------------------------------------------------------

        Suite("Ctrl+C after the launch command (the orphan rule)");

        // Session 20260918-161828-81e679: the Steam launch went out at 16:18:29.044 and
        // the cancel landed at 16:18:29.418 — 374 ms later, four-odd seconds before the
        // game process existed. Looking once found nothing, so the run restored, deleted
        // the prepared replay and exited, and the game started behind it with nothing
        // watching it and no replay to play.
        // The moment Steam was asked, and the cancel a stated time after it. Every case
        // below is written as "the launch went out at T, the Ctrl+C landed at T + x".
        var launchedAt = new DateTime(2026, 9, 18, 16, 18, 29, DateTimeKind.Utc);
        DateTime CancelledAfter(TimeSpan gap) => launchedAt + gap;

        Test("a dump cancelled before the game appears waits for it rather than looking once", () =>
        {
            var dump = ExitPolicy.EndProcess(TimeSpan.FromSeconds(15));
            var plan = InterruptedLaunchPlan.For(
                dump, launchedAt, gameSeen: false, CancelledAfter(TimeSpan.FromMilliseconds(374)));

            Equal(InterruptedLaunchAction.WaitForItThenEnd, plan.Action);
            Equal(
                TimeSpan.FromSeconds(InterruptedLaunchPlan.AppearGraceSeconds) - TimeSpan.FromMilliseconds(374),
                plan.AppearGrace,
                "what is left of the grace, counted from the launch and not from the cancel");
            Equal(TimeSpan.FromSeconds(15), plan.CloseGrace, "and it is ended on the dump's own close grace");
        });

        // The grace exists for the seconds while Steam is still producing the game. A user
        // who has watched "Waiting for Age of Empires IV to start ..." for minutes and then
        // given up is telling the launcher the game is not coming: waiting 25 s more, and
        // then saying "if it opens, close it", would be latency and a falsehood.
        Test("a dump cancelled long after the launch waits for nothing, as it did before this rule", () =>
        {
            var plan = InterruptedLaunchPlan.For(
                ExitPolicy.EndProcess(TimeSpan.FromSeconds(15)),
                launchedAt, gameSeen: false, CancelledAfter(TimeSpan.FromMinutes(3)));

            Equal(InterruptedLaunchAction.LeaveItAlone, plan.Action);
            Equal(TimeSpan.Zero, plan.AppearGrace, "nothing is waited for and nothing is warned about");
        });

        Test("the grace runs out exactly at its own length", () =>
        {
            var full = TimeSpan.FromSeconds(InterruptedLaunchPlan.AppearGraceSeconds);
            var dump = ExitPolicy.EndProcess(TimeSpan.FromSeconds(15));

            var justInside = InterruptedLaunchPlan.For(
                dump, launchedAt, gameSeen: false, CancelledAfter(full - TimeSpan.FromSeconds(1)));
            Equal(InterruptedLaunchAction.WaitForItThenEnd, justInside.Action);
            Equal(TimeSpan.FromSeconds(1), justInside.AppearGrace);

            var atTheEdge = InterruptedLaunchPlan.For(dump, launchedAt, gameSeen: false, CancelledAfter(full));
            Equal(InterruptedLaunchAction.LeaveItAlone, atTheEdge.Action, "zero left is nothing to wait for");
            Equal(TimeSpan.Zero, atTheEdge.AppearGrace);
        });

        Test("the appear grace is its own short one, not the wait a launch nobody cancelled gets", () =>
        {
            True(InterruptedLaunchPlan.AppearGraceSeconds >= 20, "long enough for a cold Steam (~5 s measured)");
            True(InterruptedLaunchPlan.AppearGraceSeconds <= 30, "short enough that a cancelled run still feels cancelled");
            True(
                InterruptedLaunchPlan.AppearGraceSeconds < new LauncherConfig().GameStartTimeoutSeconds,
                "and far under GameStartTimeoutSeconds (300 s), which is for a launch that means to succeed");
        });

        Test("a dump cancelled with the game already up ends it straight away, as it always did", () =>
        {
            foreach (var gap in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(20) })
            {
                var plan = InterruptedLaunchPlan.For(
                    ExitPolicy.EndProcess(TimeSpan.FromSeconds(15)), launchedAt, gameSeen: true, CancelledAfter(gap));

                Equal(InterruptedLaunchAction.EndTheGame, plan.Action, $"gap={gap}");
                Equal(TimeSpan.Zero, plan.AppearGrace, "there is nothing left to wait for");
                Equal(TimeSpan.FromSeconds(15), plan.CloseGrace);
            }
        });

        Test("a watch is left exactly as it is: the user wants that game", () =>
        {
            foreach (var seen in new[] { true, false })
            {
                var plan = InterruptedLaunchPlan.For(
                    ExitPolicy.WaitForUser, launchedAt, gameSeen: seen, CancelledAfter(TimeSpan.FromSeconds(1)));
                Equal(InterruptedLaunchAction.LeaveItAlone, plan.Action, $"gameSeen={seen}");
                Equal(TimeSpan.Zero, plan.AppearGrace, $"gameSeen={seen}");
            }
        });

        Test("nothing is waited for when Steam was never asked", () =>
        {
            var plan = InterruptedLaunchPlan.For(
                ExitPolicy.EndProcess(TimeSpan.FromSeconds(15)),
                launchedAtUtc: null, gameSeen: false, CancelledAfter(TimeSpan.FromSeconds(1)));

            Equal(InterruptedLaunchAction.LeaveItAlone, plan.Action);
            Equal(TimeSpan.Zero, plan.AppearGrace, "a cancel during the download or the countdown ends at once");
        });

        Test("the line printed when it never appears promises only what is true by then", () =>
        {
            Equal(
                "Age of Empires IV had not started yet; if it opens, close it — your settings are already back.",
                DumpConsoleText.GameNeverAppeared);

            // The same moment after a restore that failed. The console has just printed
            // "Finished WITH ERRORS" and the kept backup's path above this line, so this
            // one may not say the settings are back — SessionRunner picks by the outcome.
            Equal(
                "Age of Empires IV had not started yet; if it opens, close it — your settings could not all be put back; see the backup path above.",
                DumpConsoleText.GameNeverAppearedRestoreFailed);
            False(
                DumpConsoleText.GameNeverAppearedRestoreFailed.Contains("already back", StringComparison.Ordinal),
                "the promise the other line makes is exactly what this one must not repeat");
            True(
                DumpConsoleText.GameNeverAppearedRestoreFailed.StartsWith(
                    "Age of Empires IV had not started yet; if it opens, close it", StringComparison.Ordinal),
                "and the part that is still true is said the same way");

            True(
                DumpConsoleText.WaitingForTheLaunchedGame(InterruptedLaunchPlan.AppearGraceSeconds)
                    .Contains($"{InterruptedLaunchPlan.AppearGraceSeconds} s", StringComparison.Ordinal),
                "the wait states its own length rather than looking hung");
        });

        // ---------------------------------------------------------------------------

        Suite("The shipped version");

        Test("the csproj version, the file version and the release notes name one release", () =>
        {
            var root = RepoRoot();
            // A published single-file test exe has no repository under it. This is a
            // repo-hygiene check, so there it simply does not apply.
            if (root is null) return;

            var csproj = File.ReadAllText(Path.Combine(root, "src", "Paladin.Launcher", "Paladin.Launcher.csproj"));
            var version = Between(csproj, "<Version>", "</Version>");
            NotNull(version, "the csproj has a <Version>");

            Equal($"{version}.0", Between(csproj, "<AssemblyVersion>", "</AssemblyVersion>"));
            Equal($"{version}.0", Between(csproj, "<FileVersion>", "</FileVersion>"));

            // The release workflow tags v<Version> from this csproj and publishes
            // RELEASE_NOTES.md verbatim as the release body, so a disagreement between the
            // two is a release page whose title and text name different versions.
            var notes = File.ReadAllText(Path.Combine(root, "RELEASE_NOTES.md")).Replace("\r\n", "\n");
            Equal($"# Paladin Replay Launcher {version}", notes.Split('\n')[0]);
            True(notes.Contains($"\n## New in {version}\n", StringComparison.Ordinal),
                $"the notes carry a finished '## New in {version}' section");
            False(notes.Contains($"## New in {version} (draft)", StringComparison.Ordinal),
                "and it is no longer marked a draft");
        });
    }

    /// <summary>
    /// The repository this test binary was built in, found by walking up from the binary
    /// rather than from the working directory: CI runs the suite from the repo root and a
    /// person may run it from anywhere. Null when there is no repository above it.
    /// </summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PaladinReplayLauncher.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>The text between two markers, or null if either is missing.</summary>
    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }
}
