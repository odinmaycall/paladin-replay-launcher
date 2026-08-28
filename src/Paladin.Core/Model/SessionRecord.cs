using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paladin.Core.Model;

public sealed class ReplaySourceRecord
{
    /// <summary>"local", "url", or a future provider id.</summary>
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum SessionOutcome
{
    Started,
    RestoredClean,
    RestoredChanges,
    RestoreFailed,
    RecoveredAfterCrash,
    AbandonedByUser
}

/// <summary>
/// The durable record for one replay session. Written before AoE4 launches and
/// updated as the session progresses, so a crashed launcher leaves enough on disk
/// for a later run to finish the restore.
/// </summary>
public sealed class SessionRecord
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string SessionId { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }

    /// <summary>Windows SID of the user who created the session. Recovery refuses to cross this.</summary>
    public string OwnerSid { get; set; } = "";
    public string MachineName { get; set; } = "";

    public string Aoe4DocumentsPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string QuarantinePath { get; set; } = "";
    /// <summary>Copies of what the game left behind, taken before restoring, for diffing.</summary>
    public string ChangedCopiesPath { get; set; } = "";

    public ReplaySourceRecord ReplaySource { get; set; } = new();
    /// <summary>Where the replay was placed for AoE4 to read.</summary>
    public string? PreparedReplayPath { get; set; }
    /// <summary>True when the launcher created that file and may therefore delete it during cleanup.</summary>
    public bool PreparedReplayOwnedByLauncher { get; set; }

    public string? LaunchExecutable { get; set; }
    public string? LaunchArguments { get; set; }

    public List<string> ProtectedPatterns { get; set; } = new();
    public List<FileSnapshot> PreLaunch { get; set; } = new();

    /// <summary>The crash-recovery flag. True from just before launch until a verified restore.</summary>
    public bool RestorePending { get; set; }
    public DateTime? RestoreCompletedUtc { get; set; }

    /// <summary>Updated periodically while the launcher is alive; recovery uses it to date the session.</summary>
    public DateTime LastHeartbeatUtc { get; set; }

    public List<FileChange> Changes { get; set; } = new();
    public List<RestoreResult> Restored { get; set; } = new();

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SessionOutcome Outcome { get; set; } = SessionOutcome.Started;

    public List<string> Notes { get; set; } = new();

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SessionRecord FromJson(string json) =>
        JsonSerializer.Deserialize<SessionRecord>(json, JsonOptions)
        ?? throw new InvalidDataException("Session JSON deserialised to null.");
}
