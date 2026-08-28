using System.Text;

namespace Paladin.Core.Shield;

/// <summary>
/// Separates real settings from per-launch bookkeeping.
///
/// Measured, not assumed: launching Age of Empires IV *normally* — no replay, no -dev —
/// rewrites four protected files every single time, and the only differences are
///
///     configuration_system.lua      last_modified = ...
///     configuration_user.lua        last_modified = ...
///     cloud/configuration_user.lua  last_modified = ...
///     local.ini                     app_runs_count=N, most_recently_viewed_profile_id=N
///
/// A plain content hash therefore reports "settings changed!" on every launch, which is a
/// 100% false-positive rate. Restoring on that signal reverts the game's own housekeeping
/// and, worse, would revert a setting the user deliberately changed during the session.
///
/// So each protected text file gets a second, *semantic* hash computed with the volatile
/// lines removed. Real damage changes the semantic hash; a normal launch does not.
/// </summary>
public static class VolatileContent
{
    /// <summary>Keys observed to change on every launch regardless of what the user did.</summary>
    public static readonly string[] DefaultVolatileKeys =
    {
        "last_modified",
        "app_runs_count",
        "most_recently_viewed_profile_id",
    };

    private static readonly string[] TextExtensions = { ".lua", ".ini", ".cfg", ".txt", ".json", ".xml" };

    /// <summary>
    /// Whether a semantic hash is worth attempting. Binary formats (.rkp keybindings) are
    /// left alone: any change there is treated as real, which is the safe default.
    /// </summary>
    public static bool LooksLikeText(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        return TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drops every line whose key matches a volatile key. Key is the text before the first
    /// '=', trimmed, which covers both `last_modified = 123` (lua) and `key=value` (ini).
    /// </summary>
    public static string StripVolatileLines(string content, IEnumerable<string> volatileKeys)
    {
        var keys = new HashSet<string>(volatileKeys, StringComparer.OrdinalIgnoreCase);
        var kept = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            var eq = trimmed.IndexOf('=');
            if (eq > 0)
            {
                var key = trimmed[..eq].Trim();
                // Tolerate lua table syntax like `["last_modified"]` and leading markers.
                key = key.Trim('[', ']', '"', '\'', ' ', '\t');
                if (keys.Contains(key)) continue;
            }
            kept.Append(trimmed).Append('\n');
        }

        return kept.ToString();
    }

    /// <summary>
    /// Hash of the file with volatile lines removed, or null when the file is not text,
    /// is too large, or could not be read — in which case callers must fall back to the
    /// raw hash and treat any difference as real.
    /// </summary>
    public static string? TryComputeSemanticHash(
        string fullPath, string relativePath, IEnumerable<string> volatileKeys, long maxBytes)
    {
        if (!LooksLikeText(relativePath)) return null;

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > maxBytes) return null;

            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = reader.ReadToEnd();

            var stripped = StripVolatileLines(content, volatileKeys);
            return FileHasher.HashBytes(Encoding.UTF8.GetBytes(stripped));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }
}
