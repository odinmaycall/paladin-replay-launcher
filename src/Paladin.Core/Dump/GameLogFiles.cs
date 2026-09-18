using System.Text;

namespace Paladin.Core.Dump;

/// <summary>One candidate session folder under LogFiles\.</summary>
public readonly record struct GameLogFolder(string Name, string FullPath, DateTime CreatedUtc);

/// <summary>
/// Finding the game's two log files, and reading a file the game still holds open
/// (§717 §3.2 GameLogWatcher, §3.5). The rules and the reading live in Core, with no
/// Windows API and no game, so both are tested on temp files; the launcher's
/// GameLogWatcher is the thin thing that knows where the AoE4 Documents folder is
/// and polls.
/// </summary>
public static class GameLogFiles
{
    public const string LogFilesFolder = "LogFiles";
    /// <summary>The top-level log: the only place the Version, RUN-OPTIONS and started-at lines are.</summary>
    public const string TopLevelLogName = "warnings.log";
    /// <summary>LogFiles\AoE4_&lt;stamp&gt;\warnings.&lt;date&gt;.&lt;hh-mm-ss&gt;.txt</summary>
    public const string SessionLogPattern = "warnings.*.txt";

    public static string LogFilesPathFor(string aoe4DocumentsPath) =>
        Path.Combine(aoe4DocumentsPath, LogFilesFolder);

    public static string TopLevelLogPathFor(string aoe4DocumentsPath) =>
        Path.Combine(aoe4DocumentsPath, TopLevelLogName);

    /// <summary>The folders under LogFiles\ right now, or an empty list when it does not exist yet.</summary>
    public static IReadOnlyList<GameLogFolder> ListFolders(string logFilesPath)
    {
        if (!Directory.Exists(logFilesPath)) return Array.Empty<GameLogFolder>();
        var folders = new List<GameLogFolder>();
        foreach (var path in Directory.EnumerateDirectories(logFilesPath))
        {
            DateTime created;
            try { created = Directory.GetCreationTimeUtc(path); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            folders.Add(new GameLogFolder(Path.GetFileName(path), path, created));
        }
        return folders;
    }

    /// <summary>
    /// The folder this launch made: one that was not there before the launch (the kit
    /// snapshots the names first, launch-and-dump.ps1:140, 153-158) and whose creation
    /// time is not before it. Of those, the newest by creation time, and by name when
    /// two share a time — the game's own names sort chronologically.
    ///
    /// The "not there before" test is what makes this safe when the user has hundreds of
    /// old folders; the launch time is the second guard, for the case where a folder was
    /// deleted between the snapshot and the launch and its name reappears.
    /// </summary>
    public static GameLogFolder? NewestSessionFolder(
        IEnumerable<GameLogFolder> folders, IReadOnlySet<string> namesBeforeLaunch, DateTime launchedUtc)
    {
        // A file system's creation time can round down to the second (and FAT to two), so
        // a folder made in the same second as the launch must still count.
        var floor = launchedUtc - TimeSpan.FromSeconds(2);
        GameLogFolder? best = null;
        foreach (var folder in folders)
        {
            if (namesBeforeLaunch.Contains(folder.Name)) continue;
            if (folder.CreatedUtc < floor) continue;
            if (best is null ||
                folder.CreatedUtc > best.Value.CreatedUtc ||
                (folder.CreatedUtc == best.Value.CreatedUtc && string.CompareOrdinal(folder.Name, best.Value.Name) > 0))
                best = folder;
        }
        return best;
    }

    /// <summary>The session log inside a session folder: the most recently written, and the last by name when two share a time.</summary>
    public static string? NewestSessionLog(string sessionFolder)
    {
        if (!Directory.Exists(sessionFolder)) return null;
        string? best = null;
        var bestWritten = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(sessionFolder, SessionLogPattern))
        {
            DateTime written;
            try { written = File.GetLastWriteTimeUtc(file); }
            catch (IOException) { continue; }
            if (best is null ||
                written > bestWritten ||
                (written == bestWritten && string.CompareOrdinal(Path.GetFileName(file), Path.GetFileName(best)) > 0))
            {
                best = file;
                bestWritten = written;
            }
        }
        return best;
    }

    /// <summary>
    /// The first <paramref name="lines"/> lines of the top-level warnings.log. It is
    /// 92 KB to 94 MB in the kit's copies and only its head is ever wanted, so this
    /// stops reading there. Null when the file cannot be read at all.
    /// </summary>
    public static string? ReadHead(string path, int lines = DumpEvidence.TopLevelLinesRead)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var kept = new List<string>(lines);
            for (var i = 0; i < lines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                kept.Add(line);
            }
            return string.Join("\n", kept);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a file the game is still writing: opened FileShare.ReadWrite | Delete (the
/// game holds it open and the kit had to do the same, launch-and-dump.ps1:106-110),
/// re-read from where the last read stopped, and a line is only handed on once its
/// newline has arrived — a half-written line must never be matched against a marker.
///
/// Not thread-safe: one tail, one reader, which is what the run needs.
/// </summary>
public sealed class LogTail
{
    private readonly string _path;
    private readonly List<string> _lines = new();
    private readonly StringBuilder _partial = new();
    // Kept across reads: a UTF-8 character split over a buffer boundary would otherwise
    // decode to a replacement character, and the game writes player names in UTF-8.
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _offset;
    private bool _sawCr;

    public LogTail(string path) => _path = path;

    public string Path => _path;

    /// <summary>Every complete line so far, in file order.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>What has been read but has no newline yet; kept for the next Refresh.</summary>
    public string Pending => _partial.ToString();

    /// <summary>
    /// Reads whatever was appended since the last call and returns the total line count.
    /// A file that shrank (a new run truncated it) is read again from the start.
    /// </summary>
    public int Refresh()
    {
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _offset)
            {
                _offset = 0;
                _lines.Clear();
                _partial.Clear();
                _decoder.Reset();
            }
            if (stream.Length == _offset) return _lines.Count;

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            var chars = new char[buffer.Length];
            var read = stream.Read(buffer, 0, buffer.Length);
            while (read > 0)
            {
                var decoded = _decoder.GetChars(buffer, 0, read, chars, 0);
                Append(chars, decoded);
                _offset += read;
                read = stream.Read(buffer, 0, buffer.Length);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _lines.Count;
    }

    private void Append(char[] text, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\r':
                    _sawCr = true;
                    break;
                case '\n':
                    _lines.Add(Take());
                    _sawCr = false;
                    break;
                default:
                    if (_sawCr)
                    {
                        // A lone CR is a line ending too; the game does not write them, but a
                        // half-flushed CRLF must not swallow the character after it.
                        _lines.Add(Take());
                        _sawCr = false;
                    }
                    _partial.Append(c);
                    break;
            }
        }
    }

    private string Take()
    {
        var line = _partial.ToString();
        _partial.Clear();
        if (line.Length > 0 && line[0] == '﻿') line = line[1..];
        return line;
    }

    /// <summary>The whole file as read so far, complete lines only: what the evidence is built from.</summary>
    public string Text() => string.Join("\n", _lines);
}
