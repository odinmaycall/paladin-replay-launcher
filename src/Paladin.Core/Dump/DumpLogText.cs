using System.Globalization;
using System.Text.RegularExpressions;

namespace Paladin.Core.Dump;

/// <summary>Which of the game's two log files a line came from, told by its prefix.</summary>
public enum LogLineForm
{
    /// <summary>LogFiles\AoE4_&lt;stamp&gt;\warnings.&lt;date&gt;.txt: "04:53:44.792   PALADIN3_HELLO|..." — a bare clock.</summary>
    Session,
    /// <summary>The top-level warnings.log: "(I) [04:53:19.236] [000030660]: Version [...]" — level, clock, thread.</summary>
    TopLevel,
}

/// <summary>One log line with its prefix taken off.</summary>
public readonly record struct LogLine(string Clock, string Message, LogLineForm Form);

/// <summary>PALADIN3_ENV|&lt;count&gt;|&lt;game time&gt;|&lt;dev mode&gt;. Count is null when the field was not a number (F3: the game is not signed in).</summary>
public readonly record struct EnvLine(int? Count, string RawCount, double? GameTime, bool? DevMode);

/// <summary>PALADIN2_DONE|a|b|n: PD2(a,b) finished and printed n rows.</summary>
public readonly record struct DoneLine(int A, int B, int Count);

/// <summary>PALADIN2_PLAYER|i|name|civ|team|x|z as parsePlayerRows reads it (logic.mjs:80-91): the name is taken from the end so a "|" in it survives.</summary>
public readonly record struct PlayerLine(int Index, string Name, string Civ, int Team, double? X, double? Z);

/// <summary>PALADIN2|index|id|squad|blueprint|x|y|z|owner as parseDumpLog reads it (logic.mjs:40-41); Squad is null for -1, Owner is "world", a player index, or null for "?".</summary>
public readonly record struct EntityLine(int Index, long Id, int? Squad, string Blueprint, double X, double Y, double Z, string? Owner);

/// <summary>
/// Pure classification of the lines the game writes while a dump runs (§717 §3.2).
///
/// A PALADIN marker counts only when it starts the message, right after the clock
/// prefix. The kit's substring matcher was fooled once: the 244129849 fatal line
/// quotes the pasted text ("GameObj::OnFatalScarError: [[string "...print("PALADIN2_LEN|320")..."),
/// so it "found" the marker of a line that had killed the game. Anchoring closes
/// that, and a fatal line is its own detection.
///
/// Nothing here reads a file; the watcher (pass B) hands lines in.
/// </summary>
public static class DumpLogText
{
    private static readonly Regex SessionForm =
        new(@"^﻿?(\d{2}:\d{2}:\d{2}\.\d{3})\s+(.*)$", RegexOptions.Compiled);

    private static readonly Regex TopLevelForm =
        new(@"^﻿?\([A-Z]\) \[(\d{2}:\d{2}:\d{2}\.\d{3})\] \[\d+\]: (.*)$", RegexOptions.Compiled);

    /// <summary>The design's anchor: PALADIN2 (an entity row) or any PALADIN2_*/PALADIN3_* marker, at the start of the message.</summary>
    private static readonly Regex MarkerAtStart =
        new(@"^(PALADIN2|PALADIN[23]_[A-Z_]+)\|", RegexOptions.Compiled);

    /// <summary>
    /// §868 — ANY Paladin marker at the start of the message, which is a different question from
    /// <see cref="MarkerAtStart"/>'s.
    ///
    /// THIS COST A REAL CAPTURE. The Deep bootstrap prints PALADIN9_FREEZE when it stops game time, and
    /// <see cref="HasMarker"/> validated every wait against MarkerAtStart — which admits only PALADIN2
    /// and PALADIN3. So the launcher waited for a line the game had already printed, could not
    /// structurally recognise it, and gave up on a bootstrap that had worked. The game log says exactly
    /// that: `PALADIN9_FREEZE|true|0.0|6.625`, twice, and no samples.
    ///
    /// MarkerAtStart IS DELIBERATELY NOT WIDENED. §842 keeps it narrow so a world dump's evidence can
    /// never start carrying sampler rows, and `Marker`/`IsPaladinLine` still answer that narrow
    /// question. This one exists only to answer "is the thing I am waiting for a marker at all", where
    /// the caller has already named the exact marker it wants.
    /// </summary>
    private static readonly Regex AnyMarkerAtStart =
        new(@"^(PALADIN2|PALADIN[0-9]_[A-Z0-9_]+)\|", RegexOptions.Compiled);

    /// <summary>
    /// §842 — THE DEEP SAMPLER'S OWN MARKERS, which <see cref="MarkerAtStart"/> does not admit.
    ///
    /// That alternation accepts only a literal PALADIN2, or PALADIN followed by 2 or 3. The Deep
    /// sampler prints PALADIN5_VA rows, so a Deep run reusing the dump's filter keeps the framing and
    /// ZERO rows — and says nothing about it, because the sampler's completion sentinel is
    /// PALADIN3_SQDEF, which DOES match, so the ladder is judged a success.
    ///
    /// The site's receiving end is already written against this shape: src/lib/deepCapture.ts uses
    /// /PALADIN[567]_[A-Z0-9_]*\|/ and src/lib/deepCapture.test.ts asserts that the dump filter above
    /// drops these rows today. 5, 6 and 7 are admitted together so a later sampler revision does not
    /// need this file edited again.
    /// </summary>
    private static readonly Regex DeepMarkerAtStart =
        new(@"^(PALADIN[567]_[A-Z0-9_]+)\|", RegexOptions.Compiled);

    private static readonly Regex Env =
        new(@"^PALADIN3_ENV\|([^|]*)\|([^|]*)\|([^|]*)$", RegexOptions.Compiled);

    private static readonly Regex Done =
        new(@"^PALADIN2_DONE\|(\d+)\|(\d+)\|(\d+)$", RegexOptions.Compiled);

    // The banker's own expressions, ported verbatim from logic.mjs:83 and :40-41 and anchored.
    private static readonly Regex Player =
        new(@"^PALADIN2_PLAYER\|(\d+)\|(.*)\|([^|]*)\|(-?\d+)\|(-?[\d.eE+-]*)\|(-?[\d.eE+-]*)\s*$", RegexOptions.Compiled);

    private const string Coord = @"(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";

    private static readonly Regex Entity =
        new($@"^PALADIN2\|(\d+)\|(\d+)\|(?:(-?\d+)\|)?([^|\s]+)\|{Coord}\|{Coord}\|{Coord}(?:\|(world|\d+|\?))?", RegexOptions.Compiled);

    private static readonly Regex SquadCount =
        new(@"^PALADIN3_SQ_(BEGIN|DONE)\|(\d+)$", RegexOptions.Compiled);

    private static readonly Regex MapGen =
        new(@"^MapGen -- Generating with (biome|layout|size|seed|player count) (.+)$", RegexOptions.Compiled);

    public const string MissionStartText = "GAME -- Starting mission:";
    public const string FatalText = "GameObj::OnFatalScarError";
    public const string FatalText2 = "*FATAL SCAR ERROR";
    public const string AccountPromptText = "UserPromptModalPage";
    /// <summary>The fatal the game prints when quit() is not defined: the script functions are unavailable because it is not signed in (F3).</summary>
    public const string ScriptsUnavailableText = "attempt to call a nil value (global 'quit')";

    // ---- lines ---------------------------------------------------------------------------

    /// <summary>Splits a log's text into lines, tolerating CRLF and a leading BOM (the kit's evidence copies carry one).</summary>
    public static IEnumerable<string> Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            yield return line;
        }
    }

    /// <summary>Takes the prefix off a line in either form. False for the game's unprefixed lines (checksum warnings, the banner).</summary>
    public static bool TryParse(string? raw, out LogLine line)
    {
        line = default;
        if (raw is null) return false;
        if (raw.EndsWith('\r')) raw = raw[..^1];

        var m = SessionForm.Match(raw);
        if (m.Success)
        {
            line = new LogLine(m.Groups[1].Value, m.Groups[2].Value, LogLineForm.Session);
            return true;
        }

        m = TopLevelForm.Match(raw);
        if (m.Success)
        {
            line = new LogLine(m.Groups[1].Value, m.Groups[2].Value, LogLineForm.TopLevel);
            return true;
        }

        return false;
    }

    /// <summary>The line's message without its prefix, or the whole line when it has none.</summary>
    public static string MessageOf(string raw) => TryParse(raw, out var line) ? line.Message : raw;

    // ---- markers ---------------------------------------------------------------------------

    /// <summary>
    /// The PALADIN marker that starts the message ("PALADIN2", "PALADIN3_HELLO",
    /// "PALADIN2_DONE", "PALADIN3_SQ" ...), or null. A marker anywhere else in the
    /// line — quoted inside a fatal error — is not a marker.
    /// </summary>
    public static string? Marker(string? raw)
    {
        if (!TryParse(raw, out var line)) return null;
        var m = MarkerAtStart.Match(line.Message);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>True when the line carries any PALADIN marker at its anchor: the lines the evidence keeps.</summary>
    public static bool IsPaladinLine(string? raw) => Marker(raw) is not null;

    /// <summary>§842 — the Deep marker that starts the message ("PALADIN5_VA", "PALADIN5_VA_BEGIN" ...), or null.</summary>
    public static string? DeepMarker(string? raw)
    {
        if (!TryParse(raw, out var line)) return null;
        var m = DeepMarkerAtStart.Match(line.Message);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>§842 — True for a Deep sampler line (PALADIN5_*, and 6/7 reserved for later samplers).</summary>
    public static bool IsDeepLine(string? raw) => DeepMarker(raw) is not null;

    /// <summary>
    /// §842 — Every line a DEEP capture keeps: the sampler's own rows AND the ladder framing the
    /// dump already knows, because the sampler ends on PALADIN3_SQDEF and the session's PALADIN3_HELLO
    /// and PALADIN3_ENV are the same evidence they are for a dump.
    /// </summary>
    public static bool IsDeepEvidenceLine(string? raw) => IsDeepLine(raw) || IsPaladinLine(raw);

    /// <summary>True for a printed entity row (PALADIN2|...).</summary>
    public static bool IsEntityRow(string? raw) => Marker(raw) == "PALADIN2";

    /// <summary>True when the message starts with the given marker text, e.g. "PALADIN3_HELLO|" or "PALADIN2_DONE|0|199|".</summary>
    public static bool HasMarker(string? raw, string marker) =>
        TryParse(raw, out var line) && line.Message.StartsWith(marker, StringComparison.Ordinal) && AnyMarkerAtStart.IsMatch(line.Message);

    // ---- detections -----------------------------------------------------------------------

    /// <summary>A Lua error the game will close itself over, 1-5 s later (F6). Substring by design: the whole line is the evidence.</summary>
    public static bool IsFatal(string? raw) =>
        raw is not null && (raw.Contains(FatalText, StringComparison.Ordinal) || raw.Contains(FatalText2, StringComparison.Ordinal));

    /// <summary>"GAME -- Starting mission: " — the replay is playing; the console can be opened 3 s later.</summary>
    public static bool IsMissionStart(string? raw) =>
        TryParse(raw, out var line) && line.Message.StartsWith(MissionStartText, StringComparison.Ordinal);

    /// <summary>The -dev "Account Authentication" prompt's page being loaded. Informational: the dump runs with it up.</summary>
    public static bool IsAccountPrompt(string? raw) =>
        raw is not null && raw.Contains(AccountPromptText, StringComparison.Ordinal);

    /// <summary>The game is not signed in, so quit() and the World_ functions are nil (F3).</summary>
    public static bool IsScriptsUnavailable(string? raw) =>
        raw is not null && raw.Contains(ScriptsUnavailableText, StringComparison.Ordinal);

    // ---- parsers --------------------------------------------------------------------------

    public static bool TryEnv(string? raw, out EnvLine env)
    {
        env = default;
        if (!TryParse(raw, out var line)) return false;
        var m = Env.Match(line.Message);
        if (!m.Success) return false;

        int? count = int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        double? time = double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : null;
        bool? dev = m.Groups[3].Value switch { "true" => true, "false" => false, _ => null };
        env = new EnvLine(count, m.Groups[1].Value, time, dev);
        return true;
    }

    public static bool TryDone(string? raw, out DoneLine done)
    {
        done = default;
        if (!TryParse(raw, out var line)) return false;
        var m = Done.Match(line.Message);
        if (!m.Success) return false;
        done = new DoneLine(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>The PALADIN2_DEF sentinel. <paramref name="hasNil"/> is true when a definition was dropped: one of the seven functions printed as nil (F4).</summary>
    public static bool TryDef(string? raw, out bool hasNil)
    {
        hasNil = false;
        if (!HasMarker(raw, "PALADIN2_DEF|")) return false;
        hasNil = MessageOf(raw!).Contains("|nil", StringComparison.Ordinal);
        return true;
    }

    /// <summary>The PALADIN3_SQDEF sentinel, the same way.</summary>
    public static bool TrySquadDef(string? raw, out bool hasNil)
    {
        hasNil = false;
        if (!HasMarker(raw, "PALADIN3_SQDEF|")) return false;
        hasNil = MessageOf(raw!).Contains("|nil", StringComparison.Ordinal);
        return true;
    }

    public static bool TryPlayer(string? raw, out PlayerLine player)
    {
        player = default;
        if (!TryParse(raw, out var line)) return false;
        var m = Player.Match(line.Message);
        if (!m.Success) return false;
        player = new PlayerLine(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            m.Groups[2].Value,
            m.Groups[3].Value,
            int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            Number(m.Groups[5].Value),
            Number(m.Groups[6].Value));
        return true;
    }

    public static bool TryEntity(string? raw, out EntityLine entity)
    {
        entity = default;
        if (!TryParse(raw, out var line)) return false;
        var m = Entity.Match(line.Message);
        if (!m.Success) return false;

        int? squad = null;
        if (m.Groups[3].Success)
        {
            var s = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            squad = s >= 0 ? s : null;
        }
        string? owner = m.Groups[8].Success ? (m.Groups[8].Value == "?" ? null : m.Groups[8].Value) : null;

        entity = new EntityLine(
            int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
            long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            squad,
            m.Groups[4].Value,
            double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
            double.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture),
            owner);
        return true;
    }

    /// <summary>PALADIN3_SQ_BEGIN|n or PALADIN3_SQ_DONE|n.</summary>
    public static bool TrySquadCount(string? raw, out bool isDone, out int count)
    {
        isDone = false;
        count = 0;
        if (!TryParse(raw, out var line)) return false;
        var m = SquadCount.Match(line.Message);
        if (!m.Success) return false;
        isDone = m.Groups[1].Value == "DONE";
        count = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>One of the five MapGen lines the evidence keeps: key is biome, layout, size, seed or "player count".</summary>
    public static bool TryMapGen(string? raw, out string key, out string value)
    {
        key = "";
        value = "";
        if (!TryParse(raw, out var line)) return false;
        var m = MapGen.Match(line.Message);
        if (!m.Success) return false;
        key = m.Groups[1].Value;
        value = m.Groups[2].Value;
        return true;
    }

    private static double? Number(string text) =>
        text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---- the whole log at once ------------------------------------------------------------

    /// <summary>Reads every line of a log's text into one <see cref="DumpLogSummary"/>.</summary>
    public static DumpLogSummary Summarise(string? text)
    {
        var summary = new DumpLogSummary();
        foreach (var line in Lines(text)) summary.Add(line);
        return summary;
    }
}

/// <summary>
/// What a log says about a dump so far: the markers seen, the counts, and the three
/// detections. <see cref="Complete"/> is the queue's rule (dump-queue.ps1:35-41):
/// every entity the console counted has been printed.
/// </summary>
public sealed class DumpLogSummary
{
    public bool HelloSeen { get; private set; }
    public EnvLine? Env { get; private set; }
    public int? EnvCount => Env?.Count;
    public bool DefSeen { get; private set; }
    public bool DefHasNil { get; private set; }
    public bool BeginSeen { get; private set; }
    public int EntityRows { get; private set; }
    public int ErrRows { get; private set; }
    public int PlayerRows { get; private set; }
    public int PlayerErrRows { get; private set; }
    public List<DoneLine> Chunks { get; } = new();
    public bool SquadDefSeen { get; private set; }
    public bool SquadDefHasNil { get; private set; }
    public int? SquadBegin { get; private set; }
    public int? SquadDone { get; private set; }
    public int SquadRows { get; private set; }

    public bool MissionStarted { get; private set; }
    public int AccountPrompts { get; private set; }
    public bool Fatal { get; private set; }
    /// <summary>The first fatal line, whole, for the session record.</summary>
    public string? FatalLine { get; private set; }
    public bool ScriptsUnavailable { get; private set; }

    /// <summary>The clock of the first entity row (readDumpHeader's capturedAt reads the same, logic.mjs:118).</summary>
    public string? FirstRowClock { get; private set; }
    public string? LastDoneClock { get; private set; }

    /// <summary>Rows ≥ the ENV count. False until an ENV line with a numeric count has been seen.</summary>
    public bool Complete => EnvCount is int n && EntityRows >= n;

    public void Add(string line)
    {
        if (DumpLogText.IsFatal(line))
        {
            if (!Fatal) FatalLine = line;
            Fatal = true;
            if (DumpLogText.IsScriptsUnavailable(line)) ScriptsUnavailable = true;
            return;
        }

        if (DumpLogText.IsAccountPrompt(line)) AccountPrompts++;
        if (DumpLogText.IsMissionStart(line)) MissionStarted = true;

        var marker = DumpLogText.Marker(line);
        if (marker is null) return;

        switch (marker)
        {
            case "PALADIN2":
                EntityRows++;
                if (FirstRowClock is null && DumpLogText.TryParse(line, out var row)) FirstRowClock = row.Clock;
                break;
            case "PALADIN2_ERR": ErrRows++; break;
            case "PALADIN2_PLAYER": PlayerRows++; break;
            case "PALADIN2_PLAYERERR": PlayerErrRows++; break;
            case "PALADIN2_BEGIN": BeginSeen = true; break;
            case "PALADIN2_DEF":
                DefSeen = true;
                if (DumpLogText.TryDef(line, out var nil) && nil) DefHasNil = true;
                break;
            case "PALADIN2_DONE":
                if (DumpLogText.TryDone(line, out var done))
                {
                    Chunks.Add(done);
                    if (DumpLogText.TryParse(line, out var parsed)) LastDoneClock = parsed.Clock;
                }
                break;
            case "PALADIN3_HELLO": HelloSeen = true; break;
            case "PALADIN3_ENV":
                if (DumpLogText.TryEnv(line, out var env)) Env = env;
                break;
            case "PALADIN3_SQ": SquadRows++; break;
            case "PALADIN3_SQDEF":
                SquadDefSeen = true;
                if (DumpLogText.TrySquadDef(line, out var sqNil) && sqNil) SquadDefHasNil = true;
                break;
            case "PALADIN3_SQ_BEGIN":
            case "PALADIN3_SQ_DONE":
                if (DumpLogText.TrySquadCount(line, out var isDone, out var count))
                {
                    if (isDone) SquadDone = count; else SquadBegin = count;
                }
                break;
        }
    }
}
