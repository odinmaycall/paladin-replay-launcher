using Paladin.Core.Logging;
using Paladin.Core.Model;

namespace Paladin.Core.Shield;

/// <summary>
/// Owns the on-disk session tree:
///   &lt;root&gt;/Sessions/&lt;session-id&gt;/session.json
///   &lt;root&gt;/Sessions/&lt;session-id&gt;/backup/...
///   &lt;root&gt;/Sessions/&lt;session-id&gt;/quarantine/...
///   &lt;root&gt;/Sessions/&lt;session-id&gt;/session.log
/// Every write is atomic (temp + replace) so a power cut cannot leave an unparseable
/// session.json and strand the user's settings.
/// </summary>
public sealed class SessionStore
{
    public const string SessionFileName = "session.json";

    private readonly PaladinLog _log;

    public string Root { get; }
    public string SessionsRoot => Path.Combine(Root, "Sessions");

    public SessionStore(string root, PaladinLog log)
    {
        Root = root;
        _log = log;
        Directory.CreateDirectory(SessionsRoot);
    }

    public static string NewSessionId(DateTime utcNow) =>
        $"{utcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    public string SessionDirectory(string sessionId) => Path.Combine(SessionsRoot, sessionId);
    public string SessionFile(string sessionId) => Path.Combine(SessionDirectory(sessionId), SessionFileName);
    public string BackupDirectory(string sessionId) => Path.Combine(SessionDirectory(sessionId), "backup");
    public string QuarantineDirectory(string sessionId) => Path.Combine(SessionDirectory(sessionId), "quarantine");
    public string ChangedCopiesDirectory(string sessionId) => Path.Combine(SessionDirectory(sessionId), "changed");
    public string SessionLogFile(string sessionId) => Path.Combine(SessionDirectory(sessionId), "session.log");

    public SessionRecord Create(string sessionId, DateTime utcNow, string ownerSid, string machineName)
    {
        Directory.CreateDirectory(SessionDirectory(sessionId));
        Directory.CreateDirectory(BackupDirectory(sessionId));

        var record = new SessionRecord
        {
            SessionId = sessionId,
            StartedUtc = utcNow,
            LastHeartbeatUtc = utcNow,
            OwnerSid = ownerSid,
            MachineName = machineName,
            BackupPath = BackupDirectory(sessionId),
            QuarantinePath = QuarantineDirectory(sessionId),
            ChangedCopiesPath = ChangedCopiesDirectory(sessionId),
            RestorePending = false,
        };
        Save(record);
        return record;
    }

    public void Save(SessionRecord record)
    {
        var target = SessionFile(record.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var temp = target + ".tmp";
        File.WriteAllText(temp, record.ToJson());
        if (File.Exists(target))
            File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else
            File.Move(temp, target);
    }

    public void Heartbeat(SessionRecord record)
    {
        record.LastHeartbeatUtc = DateTime.UtcNow;
        try { Save(record); }
        catch (IOException ex) { _log.Warn($"Heartbeat write failed: {ex.Message}"); }
    }

    public SessionRecord? TryLoad(string sessionId)
    {
        var file = SessionFile(sessionId);
        if (!File.Exists(file)) return null;
        try { return SessionRecord.FromJson(File.ReadAllText(file)); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log.Warn($"Unreadable session record {file}: {ex.Message}");
            return null;
        }
    }

    public IEnumerable<SessionRecord> AllSessions()
    {
        if (!Directory.Exists(SessionsRoot)) yield break;
        foreach (var dir in Directory.GetDirectories(SessionsRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var record = TryLoad(Path.GetFileName(dir));
            if (record is not null) yield return record;
        }
    }

    /// <summary>
    /// Sessions this machine and this user left unfinished. Crossing either boundary is
    /// refused outright — a backup taken under someone else's profile must never be
    /// written into the current user's settings.
    /// </summary>
    public List<SessionRecord> FindPendingRestores(string ownerSid, string machineName) =>
        AllSessions()
            .Where(s => s.RestorePending)
            .Where(s => string.Equals(s.OwnerSid, ownerSid, StringComparison.OrdinalIgnoreCase))
            .Where(s => string.Equals(s.MachineName, machineName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.StartedUtc)
            .ToList();

    /// <summary>Removes completed session folders beyond the keep count. Pending ones are never removed.</summary>
    public void Prune(int keepRecent)
    {
        var completed = AllSessions()
            .Where(s => !s.RestorePending && s.Outcome != SessionOutcome.RestoreFailed)
            .OrderByDescending(s => s.StartedUtc)
            .Skip(Math.Max(0, keepRecent))
            .ToList();

        foreach (var session in completed)
        {
            try
            {
                Directory.Delete(SessionDirectory(session.SessionId), recursive: true);
                _log.Debug($"Pruned old session {session.SessionId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"Could not prune session {session.SessionId}: {ex.Message}");
            }
        }
    }
}
