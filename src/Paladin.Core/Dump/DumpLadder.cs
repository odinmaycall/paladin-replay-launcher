namespace Paladin.Core.Dump;

/// <summary>
/// The Lua the launcher types into the game's developer console to print the map
/// (§717). Every line is a constant copied byte for byte from the owner's dump kit
/// (console-paste-hello.txt, console-paste-entities-chunk.txt,
/// console-paste-squads.txt), because those exact lines are what 106 banked dumps
/// were printed with and what the banker (scripts/world-dump/logic.mjs) reads.
///
/// Two facts about the console shape everything here:
///
///   - A pasted line over the console's cap is dropped whole, not truncated: a
///     768-character line lands and a 964-character one does not (measured on
///     17 Sept, launch-and-dump.ps1:9-10). The ladders are cut so that no line
///     exceeds <see cref="MaxLadderLineLength"/> before the guard.
///   - The console's input box can hold residue from the game's own text
///     ("__Internal_Game_Autosave", "__Internal_Game_Quicksave") and a paste is
///     appended to it. Six of the kit's runs died on that: the joined line was a
///     call to a nil global and the game closed itself 1-5 s later. Every line is
///     therefore prefixed with <see cref="GuardPrefix"/>: joined to the residue it
///     becomes "__Internal_Game_Autosave_=nil print(...)" — an assignment to a new
///     global followed by the real statement, legal Lua — and on its own "_=nil
///     print(...)" parses just the same.
///
/// Pure constants and arithmetic; nothing here touches a window or a keyboard.
/// </summary>
public static class DumpLadder
{
    /// <summary>The residue guard: an assignment that swallows whatever the console's input box already held.</summary>
    public const string GuardPrefix = "_=nil ";

    /// <summary>Indices per PD2 chunk; 200 prints in 3.0-3.1 s (§717 §3.3 step 7).</summary>
    public const int ChunkSize = 200;

    /// <summary>The kit's own cap on a ladder line, before the guard (the longest is 189).</summary>
    public const int MaxLadderLineLength = 190;

    /// <summary>The longest guarded ladder line (189 + the guard); the design's stated invariant.</summary>
    public const int MaxGuardedLadderLineLength = 195;

    /// <summary>The HELLO line is the one exception: 250 characters, 256 guarded.</summary>
    public const int GuardedHelloLength = 256;

    /// <summary>Proven to land on the console (launch-and-dump.ps1:9-10); 964 is proven not to.</summary>
    public const int ProvenConsoleLineCap = 768;

    // ---- the lines, byte for byte from the kit --------------------------------------

    /// <summary>console-paste-hello.txt: proves the console opened (HELLO) and reports the entity count (ENV).</summary>
    public const string Hello =
        """print("PALADIN3_HELLO|"..tostring(pcall).."|"..tostring(World_GetNumEntities)); print("PALADIN3_ENV|"..tostring(select(2,pcall(World_GetNumEntities))).."|"..tostring(select(2,pcall(World_GetGameTime))).."|"..tostring(select(2,pcall(Misc_IsDevMode))))""";

    /// <summary>console-paste-entities-chunk.txt: the entity printers, ending in the PALADIN2_DEF sentinel.</summary>
    public static readonly IReadOnlyList<string> EntityLadder = new[]
    {
        """function PQ(e) if Entity_IsPartOfSquad(e) then return Squad_GetID(Entity_GetSquad(e)) end return -1 end""",
        """function PP(e) local p=Entity_GetPosition(e) return p.x.."|"..p.y.."|"..p.z end""",
        """function PO1(e) if World_OwnsEntity(e) then return "world" end return World_GetPlayerIndex(Entity_GetPlayerOwner(e)) end""",
        """function PO(e) local ok,r=pcall(PO1,e) if ok then return tostring(r) end return "?" end""",
        """function PR2(i) local e=World_GetEntity(i) print("PALADIN2|"..i.."|"..Entity_GetID(e).."|"..PQ(e).."|"..BP_GetName(Entity_GetBlueprint(e)).."|"..PP(e).."|"..PO(e)) end""",
        """function PD2(a,b) local n=0 for i=a,b do if pcall(PR2,i) then n=n+1 else print("PALADIN2_ERR|"..i) end end print("PALADIN2_DONE|"..a.."|"..b.."|"..n) end""",
        """function PN1(p) local n=Player_GetDisplayName(p) local ok,s=pcall(Loc_ToAnsi,n) if ok then return tostring(s) end return tostring(n) end""",
        """function PN(p) return PN1(p).."|"..tostring(Player_GetRaceName(p)).."|"..tostring(Player_GetTeam(p)) end""",
        """function PL1(i) local p=World_GetPlayerAt(i) local ok,s=pcall(Player_GetStartingPosition,p) if not ok then s={x="",z=""} end print("PALADIN2_PLAYER|"..i.."|"..PN(p).."|"..s.x.."|"..s.z) end""",
        """function PL() for i=1,World_GetPlayerCount() do if not pcall(PL1,i) then print("PALADIN2_PLAYERERR|"..i) end end end""",
        """print("PALADIN2_DEF|"..tostring(PQ).."|"..tostring(PP).."|"..tostring(PO1).."|"..tostring(PO).."|"..tostring(PR2).."|"..tostring(PD2).."|"..tostring(PL))""",
    };

    /// <summary>Marks the dump's start with the live entity count and game time, then prints the player rows (launch-and-dump.ps1:240).</summary>
    public const string Begin =
        """print("PALADIN2_BEGIN|"..World_GetNumEntities().."|"..World_GetGameTime()) PL()""";

    /// <summary>console-paste-squads.txt: the squad printers, ending in the PALADIN3_SQDEF sentinel. Typed only with --squads.</summary>
    public static readonly IReadOnlyList<string> SquadLadder = new[]
    {
        """function SO1(s) if World_OwnsSquad(s) then return "world" end return World_GetPlayerIndex(Squad_GetPlayerOwner(s)) end""",
        """function SO(s) local ok,r=pcall(SO1,s) if ok then return tostring(r) end return "?" end""",
        """function SP(s) if Squad_Count(s)==0 then return "||" end local p=Squad_GetPositionDeSpawned(s) return p.x.."|"..p.y.."|"..p.z end""",
        """function SF(s) if Squad_Count(s)==0 then return "" end return tostring(Entity_GetID(Squad_GetFirstEntity(s))) end""",
        """function SR(gid,i,s) print("PALADIN3_SQ|"..i.."|"..Squad_GetID(s).."|"..BP_GetName(Squad_GetBlueprint(s)).."|"..SP(s).."|"..Squad_Count(s).."|"..SF(s).."|"..SO(s)) end""",
        """function SR2(gid,i,s) if not pcall(SR,gid,i,s) then print("PALADIN3_SQERR|"..i) end return false end""",
        """function SQ1() PALN=(PALN or 0)+1 local g=SGroup_Create("pal"..PALN) World_GetAllSquads(g) return g end""",
        """function SQ() local g=SQ1() print("PALADIN3_SQ_BEGIN|"..SGroup_Count(g)) SGroup_ForEach(g,SR2) print("PALADIN3_SQ_DONE|"..SGroup_Count(g)) end""",
        """print("PALADIN3_SQDEF|"..tostring(SO).."|"..tostring(SP).."|"..tostring(SF).."|"..tostring(SR).."|"..tostring(SR2).."|"..tostring(SQ1).."|"..tostring(SQ))""",
    };

    /// <summary>Runs the squad dump (launch-and-dump.ps1:253).</summary>
    public const string SquadCall =
        """if not pcall(SQ) then print("PALADIN3_SQERR|begin") end""";

    /// <summary>Closes the game. Sent, never waited for (§717 decision 5).</summary>
    public const string Quit = "quit()";

    /// <summary>The line-cap probe lengths of console-paste-len.txt; diagnostic only (--dump-diagnose).</summary>
    public static readonly IReadOnlyList<int> ProbeLengths = new[] { 320, 512, 768 };

    // ---- the markers each line prints -------------------------------------------------

    public const string HelloMarker = "PALADIN3_HELLO|";
    public const string EnvMarker = "PALADIN3_ENV|";
    public const string DefMarker = "PALADIN2_DEF|";
    public const string BeginMarker = "PALADIN2_BEGIN|";
    public const string SquadDefMarker = "PALADIN3_SQDEF|";
    public const string SquadBeginMarker = "PALADIN3_SQ_BEGIN|";
    public const string SquadDoneMarker = "PALADIN3_SQ_DONE|";

    /// <summary>What PD2(a,b) prints when it has finished: "PALADIN2_DONE|a|b|" followed by the row count.</summary>
    public static string DoneMarker(int a, int b) => $"PALADIN2_DONE|{a}|{b}|";

    // ---- building lines -------------------------------------------------------------------

    /// <summary>The line as it is delivered to the console: the residue guard, then the Lua.</summary>
    public static string Guard(string line) => GuardPrefix + line;

    /// <summary>PD2(a,b): prints rows a..b inclusive and the DONE line.</summary>
    public static string Chunk(int a, int b) => $"PD2({a},{b})";

    /// <summary>
    /// A line-cap probe. The kit's probes were 320/512/768 characters unguarded; here
    /// the padding is shortened by the guard's length so the DELIVERED line is exactly
    /// <paramref name="guardedLength"/> characters, which is the thing being probed.
    /// </summary>
    public static string Probe(int guardedLength)
    {
        var head = $"print(\"PALADIN2_LEN|{guardedLength}\") --";
        var padding = guardedLength - GuardPrefix.Length - head.Length;
        if (padding < 0)
            throw new ArgumentOutOfRangeException(nameof(guardedLength), $"a probe must be at least {GuardPrefix.Length + head.Length} characters");
        return head + new string('x', padding);
    }

    /// <summary>
    /// The (a, b) index ranges that cover 0..n-1 in chunks of <paramref name="chunkSize"/>:
    /// for 1,465 entities (0,199), (200,399), ... (1400,1464). The dump covers the
    /// indices the ENV line counted, 0..n-1 (§717 decision 6).
    /// </summary>
    public static IReadOnlyList<(int A, int B)> Chunks(int entityCount, int chunkSize = ChunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        var chunks = new List<(int, int)>();
        for (var a = 0; a < entityCount; a += chunkSize)
            chunks.Add((a, Math.Min(a + chunkSize - 1, entityCount - 1)));
        return chunks;
    }

    /// <summary>One line to deliver and the marker that proves it ran (null when nothing is printed for it).</summary>
    public sealed record ConsoleLine(string Text, string? Marker);

    /// <summary>
    /// Everything typed after the console has been proven with <see cref="Hello"/> and the
    /// ENV line has given the entity count: the entity ladder (its sentinel proves the
    /// whole ladder landed), BEGIN, one PD2 per chunk, the squad ladder and call when
    /// asked for, and quit(). Every line is guarded. The caller waits for each marker
    /// and applies the retry and focus rules of §717 §3.6; this is only the script.
    /// </summary>
    public static IReadOnlyList<ConsoleLine> Script(int entityCount, bool includeSquads, int chunkSize = ChunkSize)
    {
        var lines = new List<ConsoleLine>();

        for (var i = 0; i < EntityLadder.Count; i++)
            lines.Add(new ConsoleLine(Guard(EntityLadder[i]), i == EntityLadder.Count - 1 ? DefMarker : null));

        lines.Add(new ConsoleLine(Guard(Begin), BeginMarker));

        foreach (var (a, b) in Chunks(entityCount, chunkSize))
            lines.Add(new ConsoleLine(Guard(Chunk(a, b)), DoneMarker(a, b)));

        if (includeSquads)
        {
            for (var i = 0; i < SquadLadder.Count; i++)
                lines.Add(new ConsoleLine(Guard(SquadLadder[i]), i == SquadLadder.Count - 1 ? SquadDefMarker : null));
            lines.Add(new ConsoleLine(Guard(SquadCall), SquadBeginMarker));
        }

        lines.Add(new ConsoleLine(Guard(Quit), null));
        return lines;
    }
}
