using System.Runtime.Versioning;
using Paladin.Core.Dump;
using Paladin.Core.Logging;

namespace Paladin.Launcher.Platform;

/// <summary>
/// The game's own log, while it is being written (§717 §3.2, §3.5).
///
/// Before the launch it remembers the folder names under LogFiles\; after the process
/// appears it waits for the new folder this launch made and for the
/// warnings.&lt;date&gt;.&lt;time&gt;.txt inside it, then tails that one file. At the end it reads
/// the head of the top-level warnings.log, which is the only place the Version,
/// RUN-OPTIONS and started-at lines live. Neither file is copied and nothing else in
/// either of them is read.
///
/// The rules and the reading are <see cref="GameLogFiles"/> and <see cref="LogTail"/> in
/// Paladin.Core, where the tests can reach them (the test project references Core only);
/// what is left here is the polling and the AoE4 folder.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameLogWatcher : IDumpLogSource
{
    /// <summary>How often the folder, the file and the tail are looked at (the kit polled at 1 s; 250 ms costs nothing and sharpens every marker's timing).</summary>
    public const int PollMs = 250;

    private readonly string _logFilesPath;
    private readonly string _topLevelPath;
    private readonly HashSet<string> _foldersBeforeLaunch;
    private readonly DateTime _launchedUtc;
    private readonly PaladinLog _log;
    private LogTail? _tail;

    public GameLogWatcher(string aoe4DocumentsPath, DateTime launchedUtc, PaladinLog log)
    {
        _logFilesPath = GameLogFiles.LogFilesPathFor(aoe4DocumentsPath);
        _topLevelPath = GameLogFiles.TopLevelLogPathFor(aoe4DocumentsPath);
        _launchedUtc = launchedUtc;
        _log = log;
        _foldersBeforeLaunch = GameLogFiles.ListFolders(_logFilesPath).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _log.Debug($"LogFiles held {_foldersBeforeLaunch.Count} folder(s) before the launch.");
    }

    public string? SessionLogPath => _tail?.Path;

    /// <summary>The watched LogFiles folder, without the user name: what a "no log appeared" message names.</summary>
    public string LogFilesDisplayPath => PathDisplay.ForConsole(_logFilesPath);

    public string? SessionFolder { get; private set; }

    public IReadOnlyList<string> Lines => _tail?.Lines ?? (IReadOnlyList<string>)Array.Empty<string>();

    public int Refresh() => _tail?.Refresh() ?? 0;

    public async Task<bool> WaitForSessionLogAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (SessionFolder is null)
            {
                var folder = GameLogFiles.NewestSessionFolder(
                    GameLogFiles.ListFolders(_logFilesPath), _foldersBeforeLaunch, _launchedUtc);
                if (folder is { } found)
                {
                    SessionFolder = found.FullPath;
                    _log.Info($"The game's session log folder is {found.Name}.");
                }
            }

            if (SessionFolder is not null && GameLogFiles.NewestSessionLog(SessionFolder) is { } file)
            {
                _tail = new LogTail(file);
                _tail.Refresh();
                _log.Info($"Tailing {Path.GetFileName(file)} ({_tail.Lines.Count} lines so far).");
                return true;
            }

            await Task.Delay(PollMs, ct);
        }

        _log.Warn($"No new LogFiles session folder with a {GameLogFiles.SessionLogPattern} appeared within {timeout.TotalSeconds:0} s.");
        return false;
    }

    public async Task<int> WaitForLineAsync(int fromIndex, Func<string, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var searched = Math.Max(0, fromIndex);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Refresh();
            for (var i = searched; i < Lines.Count; i++)
                if (predicate(Lines[i]))
                    return i;
            searched = Lines.Count;

            if (DateTime.UtcNow >= deadline) return -1;
            await Task.Delay(PollMs, ct);
        }
    }

    /// <summary>Everything read so far, complete lines only — what the evidence is built from.</summary>
    public string TextSoFar => _tail?.Text() ?? "";

    /// <summary>The top-level log's first 12 lines — the Version, RUN-OPTIONS and started-at lines, and nothing past them.</summary>
    public string? ReadTopLevelHead() => GameLogFiles.ReadHead(_topLevelPath);
}
