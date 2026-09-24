using System.Text;
using System.Text.RegularExpressions;

namespace Paladin.Core.Dump;

public sealed class DumpEvidenceException : Exception
{
    public DumpEvidenceException(string message) : base(message) { }
}

/// <summary>
/// What leaves the machine, and nothing else (§717 §3.2, §3.5, §6): eight header
/// lines and the printed PALADIN rows.
///
/// From the top-level warnings.log, and only from its first
/// <see cref="TopLevelLinesRead"/> lines, exactly three lines by their exact forms:
///   - "RelicCardinal started at yyyy-mm-dd hh:mm" — cut there; the rest names the time zone
///   - "RUN-OPTIONS [-dev -replay playback:AgeIV_Replay_&lt;id&gt;]" — names the replay that played
///   - "(I) [clock] [thread]: Version [16.3.11308.0] Info [...]" — the game build
/// Never WORKING-DIR, USER, COMPUTER, LOCALE, the OS line or the "modulefilename" install path,
/// which sit between and around them, nor the "Current Steam name is [...]" line (17) and the
/// "Using [C:\Users\...]" folder lines (28-29) past the cut: <see cref="Build"/> refuses
/// outright if one slips in, and its refusal names the form, never the line.
///
/// From the session file: the five "MapGen -- Generating with" lines (biome, layout,
/// size, seed, player count) and every line whose anchored marker is a PALADIN one,
/// verbatim with its clock prefix. That text — header, newline, rows — is exactly
/// what scripts/world-dump/run.mjs reads (readDumpHeader, logic.mjs:111-134;
/// parseDumpLog, :93-104), so the owner promotes a user's dump with no new tooling.
/// </summary>
public sealed class DumpEvidence
{
    /// <summary>The top-level warnings.log is read this far and never further (it runs to 94 MB in the kit's copies).</summary>
    public const int TopLevelLinesRead = 12;

    /// <summary>3 from the top-level log + 5 MapGen lines.</summary>
    public const int HeaderLineCount = 8;

    private const string StartedAtPrefix = "RelicCardinal started at ";
    private const string RunOptionsPrefix = "RUN-OPTIONS [";

    private static readonly Regex StartedAtForm =
        new(@"^RelicCardinal started at \d{4}-\d{2}-\d{2} \d{2}:\d{2}", RegexOptions.Compiled);

    private static readonly Regex RunOptionsForm =
        new(@"^RUN-OPTIONS \[[^\]]*\]", RegexOptions.Compiled);

    private static readonly Regex VersionForm =
        new(@"^Version \[([\d.]+)\]", RegexOptions.Compiled);

    private static readonly string[] MapGenKeys = { "biome", "layout", "size", "seed", "player count" };

    /// <summary>
    /// Forms that must never leave the machine, checked on every line kept as defence in
    /// depth: the design's four (§5.2 step 2: three bare line starts and the install path's
    /// word), the LOCALE line the top-level log carries beside them — which this class, the
    /// README and the release notes all promise is refused, so it is refused — and the two
    /// the real logs carry beyond all of those: the Steam account name (top-level line 17,
    /// session line 3; a message start after either clock prefix) and any Windows user path
    /// ("Using [C:\Users\...]", top-level 28-29, session 14-15). The Worker's list wants the
    /// same two.
    /// </summary>
    private static readonly string[] PersonalStarts = { "USER [", "COMPUTER [", "WORKING-DIR [", "LOCALE [" };
    private static readonly string[] PersonalMessageStarts = { "GAME -- Current Steam name is [" };
    private static readonly string[] PersonalContains = { "modulefilename", @":\Users\" };

    public IReadOnlyList<string> HeaderLines { get; }
    public IReadOnlyList<string> RowLines { get; }
    public DumpLogSummary Summary { get; }

    /// <summary>"2026-09-18 04:53", the launch's local date and minute, or null when the top-level log lacked it.</summary>
    public string? StartedAt { get; }
    /// <summary>"-dev -replay playback:AgeIV_Replay_249960029", or null.</summary>
    public string? RunOptions { get; }
    /// <summary>"16.3.11308.0", or null.</summary>
    public string? GameVersion { get; }
    public string? Biome { get; }
    public string? Layout { get; }
    public string? Size { get; }
    /// <summary>The seed as the game prints it in decimal (831079387 for 0x318943DB), or null.</summary>
    public long? Seed { get; }
    public int? PlayerCount { get; }

    private DumpEvidence(IReadOnlyList<string> header, IReadOnlyList<string> rows, DumpLogSummary summary)
    {
        HeaderLines = header;
        RowLines = rows;
        Summary = summary;

        foreach (var line in header)
        {
            if (StartedAtForm.IsMatch(line)) StartedAt = line[StartedAtPrefix.Length..];
            else if (RunOptionsForm.IsMatch(line)) RunOptions = line[RunOptionsPrefix.Length..^1];
            else if (DumpLogText.TryParse(line, out var parsed) && VersionForm.Match(parsed.Message) is { Success: true } v) GameVersion = v.Groups[1].Value;
            else if (DumpLogText.TryMapGen(line, out var key, out var value))
            {
                switch (key)
                {
                    case "biome": Biome = value; break;
                    case "layout": Layout = value; break;
                    case "size": Size = value; break;
                    case "seed":
                        // "1176837288 (0x46251CA8)" — the decimal is what readDumpHeader takes (logic.mjs:119)
                        var decimalPart = value.Split(' ')[0];
                        if (long.TryParse(decimalPart, out var seed)) Seed = seed;
                        break;
                    case "player count":
                        if (int.TryParse(value, out var players)) PlayerCount = players;
                        break;
                }
            }
        }
    }

    public string Header => string.Join("\n", HeaderLines);
    public string Rows => string.Join("\n", RowLines);
    /// <summary>Header, newline, rows: the file run.mjs banks and the Worker's raw:&lt;id&gt; key.</summary>
    public string Text => Header + "\n" + Rows;
    public long Bytes => Encoding.UTF8.GetByteCount(Text);

    public bool HeaderComplete => HeaderLines.Count == HeaderLineCount;
    public int EntityRows => Summary.EntityRows;
    public int? EnvCount => Summary.EnvCount;
    /// <summary>Rows ≥ the ENV count: the queue's completeness rule.</summary>
    public bool Complete => Summary.Complete;
    public int ErrCount => Summary.ErrRows;
    public int Players => Summary.PlayerRows;
    public int Chunks => Summary.Chunks.Count;

    /// <summary>RUN-OPTIONS names the replay of this game (run.mjs:53; a "_paladin" clash suffix still passes).</summary>
    public bool NamesReplay(long gameId) =>
        RunOptions is not null && RunOptions.Contains($"AgeIV_Replay_{gameId}", StringComparison.Ordinal);

    /// <summary>
    /// Builds the evidence. Throws <see cref="DumpEvidenceException"/> if a personal
    /// line would be kept; that cannot happen through the selectors below, and the
    /// check is there so that it never can. The message names the matching form only:
    /// exception messages reach the console (Program.cs "Fatal error: ...") and dump.json's
    /// Failure line, which is what people screenshot and attach.
    /// </summary>
    public static DumpEvidence Build(string? topLevelText, string? sessionText)
    {
        var header = new List<string>();
        header.AddRange(TopLevelHeaderLines(topLevelText));
        header.AddRange(MapGenLines(sessionText));

        var rows = PaladinLines(sessionText);

        var form = PersonalForm(header) ?? PersonalForm(rows);
        if (form is not null)
            throw new DumpEvidenceException($"refusing to build evidence: a line of the form '{form}' would be kept");

        return new DumpEvidence(header, rows, DumpLogText.Summarise(sessionText));
    }

    /// <summary>
    /// The three top-level lines, in the order the envelope carries them: started-at
    /// (cut to the minute), RUN-OPTIONS, the Version line verbatim. Only the first
    /// <see cref="TopLevelLinesRead"/> lines are looked at; each form is taken once.
    /// </summary>
    public static IReadOnlyList<string> TopLevelHeaderLines(string? topLevelText)
    {
        string? started = null, runOptions = null, version = null;

        foreach (var line in DumpLogText.Lines(topLevelText).Take(TopLevelLinesRead))
        {
            if (started is null && StartedAtForm.Match(line) is { Success: true } s) started = s.Value;
            else if (runOptions is null && RunOptionsForm.Match(line) is { Success: true } r) runOptions = r.Value;
            else if (version is null && DumpLogText.TryParse(line, out var parsed)
                     && parsed.Form == LogLineForm.TopLevel && VersionForm.IsMatch(parsed.Message)) version = line;
        }

        var lines = new List<string>(3);
        if (started is not null) lines.Add(started);
        if (runOptions is not null) lines.Add(runOptions);
        if (version is not null) lines.Add(version);
        return lines;
    }

    /// <summary>The five MapGen lines from the session file, verbatim, in file order; each key once. The sixth ("players locations") is not kept.</summary>
    public static IReadOnlyList<string> MapGenLines(string? sessionText)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>(5);
        foreach (var line in DumpLogText.Lines(sessionText))
        {
            if (!DumpLogText.TryMapGen(line, out var key, out _)) continue;
            if (!MapGenKeys.Contains(key) || !seen.Add(key)) continue;
            lines.Add(line);
            if (lines.Count == MapGenKeys.Length) break;
        }
        return lines;
    }

    /// <summary>Every line whose anchored marker is a PALADIN one, verbatim with its clock prefix.</summary>
    public static IReadOnlyList<string> PaladinLines(string? sessionText) =>
        DumpLogText.Lines(sessionText).Where(DumpLogText.IsPaladinLine).ToList();

    /// <summary>
    /// §842 — Every line a DEEP capture's evidence keeps, verbatim with its clock prefix.
    ///
    /// Separate from <see cref="PaladinLines"/> rather than a widening of it: a world dump must not
    /// start carrying sampler rows if a sampler ever runs beside one, and a Deep capture must not be
    /// judged by the dump's own row expectations.
    /// </summary>
    public static IReadOnlyList<string> DeepLines(string? sessionText) =>
        DumpLogText.Lines(sessionText).Where(DumpLogText.IsDeepEvidenceLine).ToList();

    /// <summary>The first line that must not leave the machine, or null. What the Worker's personal_lines check (§5.2) refuses, applied here first.</summary>
    public static string? PersonalLine(IEnumerable<string> lines) =>
        lines.FirstOrDefault(line => PersonalForm(line) is not null);

    /// <summary>The form of the first line that must not leave the machine, or null: safe to print, because it is never the line.</summary>
    public static string? PersonalForm(IEnumerable<string> lines) =>
        lines.Select(line => PersonalForm(line)).FirstOrDefault(form => form is not null);

    /// <summary>The name of the personal form <paramref name="line"/> matches, or null.</summary>
    public static string? PersonalForm(string line)
    {
        foreach (var start in PersonalStarts)
            if (line.StartsWith(start, StringComparison.Ordinal)) return start;

        var message = DumpLogText.MessageOf(line);
        foreach (var start in PersonalMessageStarts)
            if (message.StartsWith(start, StringComparison.Ordinal)) return start;

        foreach (var word in PersonalContains)
            if (line.Contains(word, StringComparison.OrdinalIgnoreCase)) return word;

        return null;
    }
}
