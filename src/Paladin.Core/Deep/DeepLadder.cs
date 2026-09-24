using System.Reflection;

namespace Paladin.Core.Deep;

/// <summary>
/// §867 — THE DEEP BOOTSTRAP: the lines that install the villager sampler, in the order they go in.
///
/// This is the kit's proven frozen bootstrap, line for line, with ONE change that the launcher is able
/// to make and the kit is not: THE SELF-CHECK RUNS BEFORE THE SAMPLE.
///
/// WHY THAT ONE CHANGE IS THE WHOLE POINT. Paladin's own measurement of twenty real launches found four
/// failures and every one of them was a DROPPED PASTE LINE — the console channel loses a definition and
/// says nothing. The sampler already detects it: V5WHO() names any helper that is nil, and SAMPLE5
/// refuses to sample and prints PALADIN5_NOFN. But the kit calls SQ() in the same paste, so the damage
/// is only visible afterwards, as a replay that ran fifteen minutes and produced no rows at all. Two of
/// the four failures were exactly that, and each cost seven minutes before the driver gave up.
///
/// The launcher sends line by line and reads the log between lines, so it can ask "did everything land?"
/// BEFORE it commits, and re-send what did not. A dropped line becomes a two-second repair instead of a
/// dead capture.
///
/// THE FREEZE STAYS EARLY, and that is a measurement rather than a preference. The launcher pastes at
/// AfterPasteMs + AfterEnterMs = 1.4 s a line, so the fifteen definition lines cost about 24 seconds of
/// game time at 1x. Freezing after them would put the first Deep sample past 30 s and hand Standard the
/// opening of every sheet — the exact dependency this whole effort exists to remove. Frozen, they cost
/// nothing, because game time does not advance.
///
/// NOTHING HERE IS NEW LUA. Every definition line is byte-identical to the bootstrap that produced the
/// sixteen successful captures, shipped as an embedded resource rather than retyped into C# string
/// literals, because a silent transcription error in a Lua definition is indistinguishable from the
/// dropped line this file exists to catch.
/// </summary>
public static class DeepLadder
{
    /// <summary>The longest line that has ever landed intact. A 551-character line was measured truncated.</summary>
    public const int MaxLine = 411;

    /// <summary>The console is open and the game is answering. Printed by the kit's own hello line.</summary>
    public const string HelloMarker = "PALADIN3_HELLO";

    /// <summary>The entity count and the game clock, from the same paste as the hello.</summary>
    public const string EnvMarker = "PALADIN3_ENV";

    /// <summary>Simulation stopped. `PALADIN9_FREEZE|&lt;ok&gt;|&lt;rate&gt;|&lt;gameTime&gt;`.</summary>
    public const string FreezeMarker = "PALADIN9_FREEZE";

    /// <summary>Simulation running again. `PALADIN9_THAW|&lt;ok&gt;|&lt;rate&gt;|&lt;gameTime&gt;`.</summary>
    public const string ThawMarker = "PALADIN9_THAW";

    /// <summary>Every helper landed: the bootstrap is complete and may be committed.</summary>
    public const string AllOkMarker = "PALADIN3_SQDEF";

    /// <summary>One or more helpers are nil. The payload is V5WHO()'s trailing-comma list of their names.</summary>
    public const string MissingMarker = "PALADIN5_MISSING";

    /// <summary>SAMPLE5 declined to sample because a helper is missing. Same payload as <see cref="MissingMarker"/>.</summary>
    public const string NoFunctionMarker = "PALADIN5_NOFN";

    /// <summary>A villager row. `PALADIN5_VA|&lt;gameTime&gt;|...`, thirteen fields.</summary>
    public const string SampleMarker = "PALADIN5_VA";

    /// <summary>The sampler registered its interval and the simulation is thawed.</summary>
    public const string SquadDoneMarker = "PALADIN3_SQ_DONE";

    /// <summary>The production cadence, in game seconds between samples.</summary>
    public const int Cadence = 5;

    /// <summary>The production playback rate. SimRate is ticks per second and 8 is 1x, so 64 is 8x.</summary>
    public const int SimRate = 64;

    /// <summary>The build-order window a capture must reach before it is worth anything.</summary>
    public const int WindowSeconds = 900;

    private static readonly Lazy<IReadOnlyList<string>> _definitions = new(LoadDefinitions);

    /// <summary>
    /// The sampler's fifteen definition lines, verbatim from the embedded resource. They define the
    /// helpers (SO, V5G, V5K2, V5H, V5A, V5B, V5OK, V5CNT, V5END, SAMPLE5, V5W1..V5W3, V9T, SQ2, SQ).
    /// </summary>
    public static IReadOnlyList<string> Definitions => _definitions.Value;

    /// <summary>
    /// Stop game time dead, and say so with the rate and the clock. First, so the definition lines that
    /// follow cost no game time at all.
    /// </summary>
    public static string Freeze =>
        "local fz=pcall(Misc_SetSimRate,0) print(\"" + FreezeMarker + "|\"..tostring(fz).."
        + "\"|\"..tostring(select(2,pcall(Misc_GetSimRate))).."
        + "\"|\"..tostring(select(2,pcall(World_GetGameTime))))";

    /// <summary>
    /// DID EVERYTHING LAND? V5WHO() returns a comma list of every helper that is nil, or "" when all of
    /// them are there. This is the gate: SQ() is not sent until this answers allok.
    /// </summary>
    public static string SelfCheck =>
        "if V5WHO==nil then print(\"" + MissingMarker + "|V5WHO\") elseif V5WHO()~=\"\" then print(\""
        + MissingMarker + "|\"..V5WHO()) else print(\"" + AllOkMarker + "|allok\") end";

    /// <summary>
    /// Take the first sample while frozen, register the interval, and thaw — all in the one line the kit
    /// proved, so there is no window in which the game is frozen with nothing to thaw it.
    /// </summary>
    public static string Go => "SQ()";

    /// <summary>
    /// The way out of a frozen game when the bootstrap cannot be completed. Sent before the launcher
    /// gives up, so a replay is never abandoned at rate 0 with the console still holding it.
    /// </summary>
    public static string Thaw =>
        "local z=pcall(Misc_SetSimRate," + SimRate + ") print(\"" + ThawMarker + "|\"..tostring(z).."
        + "\"|\"..tostring(select(2,pcall(Misc_GetSimRate))).."
        + "\"|\"..tostring(select(2,pcall(World_GetGameTime))))";

    /// <summary>
    /// §867 — the names V5WHO() reported, split out of its trailing-comma list.
    ///
    /// "PALADIN5_MISSING|V5A,V5B,V5P," becomes ["V5A","V5B","V5P"]. Returns empty for any line that is
    /// not one of these markers, so a caller may pass it every line it reads.
    /// </summary>
    public static IReadOnlyList<string> MissingFrom(string? line)
    {
        if (string.IsNullOrEmpty(line)) return Array.Empty<string>();
        var at = line.IndexOf(MissingMarker + "|", StringComparison.Ordinal);
        if (at < 0) at = line.IndexOf(NoFunctionMarker + "|", StringComparison.Ordinal);
        if (at < 0) return Array.Empty<string>();

        var bar = line.IndexOf('|', at);
        var payload = line[(bar + 1)..].Trim();
        return payload
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary>Every line of the bootstrap in order, for a caller that wants to check the whole set.</summary>
    public static IReadOnlyList<string> AllLines()
    {
        var lines = new List<string> { Freeze };
        lines.AddRange(Definitions);
        lines.Add(SelfCheck);
        lines.Add(Go);
        return lines;
    }

    private static IReadOnlyList<string> LoadDefinitions()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("sampler-v5.txt", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The Deep sampler resource is missing from the assembly.");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();

        // A truncated line is a syntax error, and a syntax error is fatal in the SCAR console. Refusing
        // here turns a shipping mistake into a startup failure rather than a dead capture at 3am.
        var tooLong = lines.FirstOrDefault(l => l.Length > MaxLine);
        if (tooLong is not null)
            throw new InvalidOperationException($"A Deep sampler line is {tooLong.Length} characters and the proven cap is {MaxLine}.");

        return lines;
    }
}
