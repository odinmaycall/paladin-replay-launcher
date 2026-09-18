using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paladin.Core.Dump;

/// <summary>Serialiser settings shared by the wire shapes and the on-disk record.</summary>
public static class DumpJson
{
    /// <summary>
    /// The wire: compact, camelCase, every property written (nulls included, so the
    /// shape never varies with the data), declaration order. The same envelope
    /// serialises to the same bytes every time.
    /// </summary>
    public static JsonSerializerOptions Wire { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>The session's dump.json: the same names, indented for a human reading a failure report.</summary>
    public static JsonSerializerOptions File { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>"2026-09-17T20:05:57Z": seconds, UTC, no offset — the form the worked example carries.</summary>
    public static string Utc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>counts: { env, rows, err, players, chunks } (§717 §4.2).</summary>
public sealed class DumpCounts
{
    public int Env { get; set; }
    public int Rows { get; set; }
    public int Err { get; set; }
    public int Players { get; set; }
    public int Chunks { get; set; }
}

/// <summary>timings: { missionStartS, helloS, dumpS } — offsets from the process start, in seconds.</summary>
public sealed class DumpTimings
{
    public double MissionStartS { get; set; }
    public double HelloS { get; set; }
    public double DumpS { get; set; }
}

/// <summary>
/// The upload request's body: POST /api/world/&lt;gameId&gt; (§717 §4.2). The launcher
/// sends the raw printed rows plus the eight header lines and never builds a world
/// file (decision 1): the Worker re-parses the rows with the repo's own codec and
/// checks them against Paladin's own sidecars, which a finished file could not be.
/// Property order is the wire order; <see cref="ToJson"/> is deterministic.
/// </summary>
public sealed class DumpEnvelope
{
    public const int CurrentVersion = 1;

    /// <summary>The Worker's body cap (§5.2 step 1) and the launcher's local one before sending.</summary>
    public const long MaxBytes = 1024 * 1024;

    [JsonPropertyName("v")]
    public int V { get; set; } = CurrentVersion;
    public long GameId { get; set; }
    /// <summary>The launcher's informational version, e.g. "0.4.0".</summary>
    public string Launcher { get; set; } = "";
    /// <summary>The Shield session id the dump ran under.</summary>
    public string Session { get; set; } = "";
    /// <summary>When the last row was printed, "yyyy-MM-ddTHH:mm:ssZ".</summary>
    public string CapturedAtUtc { get; set; } = "";
    /// <summary>The build read from the replay's header, or null when unreadable.</summary>
    public int? ReplayBuild { get; set; }
    /// <summary>"16.3.11308.0" from the Version line, or null.</summary>
    public string? GameVersion { get; set; }
    /// <summary>"altshift" or "ctrlshift": the chord that opened the console.</summary>
    public string Chord { get; set; } = "";
    /// <summary>"paste" or "type": how the lines went in.</summary>
    public string Input { get; set; } = "";
    public DumpCounts Counts { get; set; } = new();
    public DumpTimings Timings { get; set; } = new();
    /// <summary>The eight header lines joined with "\n".</summary>
    public string Header { get; set; } = "";
    /// <summary>The PALADIN rows verbatim with their clock prefixes, joined with "\n".</summary>
    public string Rows { get; set; } = "";

    public string ToJson() => JsonSerializer.Serialize(this, DumpJson.Wire);

    public byte[] ToUtf8() => Encoding.UTF8.GetBytes(ToJson());

    /// <summary>The body's size on the wire. Ignored by the serialiser: it is computed FROM the serialisation.</summary>
    [JsonIgnore]
    public long SizeBytes => Encoding.UTF8.GetByteCount(ToJson());

    [JsonIgnore]
    public bool WithinCap => SizeBytes <= MaxBytes;

    public static DumpEnvelope FromJson(string json) =>
        JsonSerializer.Deserialize<DumpEnvelope>(json, DumpJson.Wire)
        ?? throw new InvalidDataException("Dump envelope JSON deserialised to null.");

    /// <summary>The envelope for a built evidence set; the caller adds what only the run knows.</summary>
    public static DumpEnvelope From(
        DumpEvidence evidence, long gameId, string launcherVersion, string sessionId, DateTime capturedAtUtc,
        int? replayBuild, string chord, string input, DumpTimings? timings = null) =>
        new()
        {
            GameId = gameId,
            Launcher = launcherVersion,
            Session = sessionId,
            CapturedAtUtc = DumpJson.Utc(capturedAtUtc),
            ReplayBuild = replayBuild,
            GameVersion = evidence.GameVersion,
            Chord = chord,
            Input = input,
            Counts = new DumpCounts
            {
                Env = evidence.EnvCount ?? 0,
                Rows = evidence.EntityRows,
                Err = evidence.ErrCount,
                Players = evidence.Players,
                Chunks = evidence.Chunks,
            },
            Timings = timings ?? new DumpTimings(),
            Header = evidence.Header,
            Rows = evidence.Rows,
        };
}

/// <summary>GET /api/world/&lt;gameId&gt; → { status: "owner"|"user"|"none", n, map, seed, uploadedAt } (§717 §4.1).</summary>
public sealed class DumpStatus
{
    public string? Status { get; set; }
    public int? N { get; set; }
    public string? Map { get; set; }
    public string? Seed { get; set; }
    public string? UploadedAt { get; set; }

    /// <summary>A world layer already exists, the owner's or a user's: a dump would be refused with "already"/"banked".</summary>
    [JsonIgnore]
    public bool HasWorld => Status is "owner" or "user";

    public static DumpStatus? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DumpStatus>(json, DumpJson.Wire); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The upload's answer (§717 §4.3): 200 { status: "stored" | "already", ... } or an
/// error body { error: "&lt;code&gt;", detail }. Every field is optional because a proxy or
/// an outage can answer with anything; the caller decides by the HTTP code first.
/// </summary>
public sealed class DumpUploadResponse
{
    public string? Status { get; set; }
    public long? GameId { get; set; }
    public string? Map { get; set; }
    public string? Seed { get; set; }
    public int? N { get; set; }
    public long? Bytes { get; set; }
    public List<string>? Verified { get; set; }
    public string? Url { get; set; }
    /// <summary>"user" on "already": whose file holds the slot.</summary>
    public string? Src { get; set; }
    public string? Error { get; set; }
    public string? Detail { get; set; }

    public static DumpUploadResponse? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DumpUploadResponse>(json, DumpJson.Wire); }
        catch (JsonException) { return null; }
    }

    /// <summary>The F10 wording: "Paladin did not accept the map: &lt;plain words&gt;." — the plain words for a code the Worker can answer.</summary>
    public static string PlainWords(string? code, string? detail = null) => code switch
    {
        "banked" => "this game already has the owner's map",
        "already" => "someone already sent this game's map",
        "no_sidecar" => "Paladin has no map data for this game yet",
        "wrong_replay" or "seed_mismatch" => "the replay that played is not this game",
        "incomplete" => "not every object was printed",
        "rate_limited" => "too many sends from your address; try again in a minute",
        "too_large" => "the map is larger than Paladin accepts",
        "uploads_closed" => "Paladin is not taking maps right now; try again later",
        null or "" => detail ?? "no reason was given",
        _ => detail is null ? code : $"{code}: {detail}",
    };
}

/// <summary>What became of the upload, in the session's dump.json.</summary>
public enum DumpUploadStatus { Pending, Done, Failed, Skipped, Rejected }

public sealed class DumpUploadRecord
{
    public DumpUploadStatus Status { get; set; } = DumpUploadStatus.Pending;
    public int? Code { get; set; }
    /// <summary>The response body, kept short.</summary>
    public string? Body { get; set; }
    public DateTime? At { get; set; }
}

public sealed class DumpChunkRecord
{
    public int A { get; set; }
    public int B { get; set; }
    public bool Done { get; set; }
    public int Count { get; set; }
    public int Tries { get; set; }
}

/// <summary>Offsets are wall-clock UTC; null until the step happens.</summary>
public sealed class DumpTimingRecord
{
    public DateTime? ProcessSeen { get; set; }
    public DateTime? MissionStart { get; set; }
    public DateTime? Hello { get; set; }
    public DateTime? Def { get; set; }
    public DateTime? LastDone { get; set; }
    public DateTime? Uploaded { get; set; }
    public DateTime? QuitSent { get; set; }
    public DateTime? Exit { get; set; }
}

/// <summary>
/// Sessions\&lt;id&gt;\dump.json (§717 §3.2): written temp+replace after every phase, so a
/// crash leaves what was known. This is what a user attaches to a failure report;
/// it holds counts, phases and timings, never a log or a personal path.
/// </summary>
public sealed class DumpRecord
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "dump.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    /// <summary>preflight, acquired, launched, mission, console, ladder, printing, evidence, uploading, closing, done, failed.</summary>
    public string Phase { get; set; } = "preflight";
    public long GameId { get; set; }
    public string? SessionId { get; set; }
    public string? Chord { get; set; }
    public string? InputMethod { get; set; }
    public int? EnvCount { get; set; }
    public double? GameTimeAtEnv { get; set; }
    public List<DumpChunkRecord> Chunks { get; set; } = new();
    public bool? DefOk { get; set; }
    public bool Fatal { get; set; }
    /// <summary>Where the evidence text was written, if it was.</summary>
    public string? EvidencePath { get; set; }
    public long? Bytes { get; set; }
    public DumpUploadRecord Upload { get; set; } = new();
    public DumpTimingRecord Timings { get; set; } = new();
    /// <summary>The one-line reason when Phase is "failed".</summary>
    public string? Failure { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, DumpJson.File);

    public static DumpRecord FromJson(string json) =>
        JsonSerializer.Deserialize<DumpRecord>(json, DumpJson.File)
        ?? throw new InvalidDataException("Dump record JSON deserialised to null.");

    /// <summary>Atomic write, the SessionStore.Save pattern: temp file, then replace.</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, ToJson());
        if (System.IO.File.Exists(path))
            System.IO.File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            System.IO.File.Move(temp, path);
    }

    public static DumpRecord? TryLoad(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try { return FromJson(System.IO.File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return null; }
    }
}
