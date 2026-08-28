using System.Text;

namespace Paladin.Core.Replay;

/// <summary>
/// Cheap sanity checks on a replay file. The point is not to fully parse a Relic
/// replay — it is to refuse to hand AoE4 an HTML error page, a login redirect or a
/// zero-byte file, and to say so clearly instead of failing inside the game.
///
/// Header layout was measured from real files on a live AoE4 install (both an
/// aoe4replays.gg-style download in playback/ and a game-recorded .rec):
///
///   offset 0  uint16  format version   (observed 0)
///   offset 2  uint16  game build       (observed 0x2c2c and 0x15e2 on different patches)
///   offset 4  ascii   "AOE4_RE"
///   offset 11 byte    0x00
///   offset 12 utf-16  recording date, e.g. "2026/7/30 ..."
/// </summary>
public static class ReplayValidator
{
    /// <summary>Smallest plausible replay. The smallest real one observed on a live install was ~3.9 KB.</summary>
    public const int MinimumPlausibleBytes = 1024;

    /// <summary>ASCII magic found at <see cref="MagicOffset"/> in every replay measured.</summary>
    public const string ExpectedMagic = "AOE4_RE";
    public const int MagicOffset = 4;

    /// <param name="IsUsable">False means do not launch this file.</param>
    /// <param name="MagicMatched">False with IsUsable true means "unknown header, proceeding anyway".</param>
    /// <param name="GameBuild">uint16 at offset 2 when readable — useful when diagnosing old-build replays.</param>
    public sealed record Verdict(bool IsUsable, bool MagicMatched, string Reason, int? GameBuild = null);

    public static Verdict Inspect(ReadOnlySpan<byte> head, long totalBytes)
    {
        if (totalBytes <= 0)
            return new Verdict(false, false, "the downloaded file is empty");

        if (totalBytes < MinimumPlausibleBytes)
            return new Verdict(false, false, $"the downloaded file is only {totalBytes} bytes, too small to be a replay");

        if (LooksLikeText(head, out var kind))
            return new Verdict(false, false, $"the download looks like {kind}, not a replay file");

        if (head.Length < MagicOffset + ExpectedMagic.Length)
            return new Verdict(true, false, "could not read enough of the header to identify it — proceeding anyway");

        var build = head[2] | (head[3] << 8);
        var magic = Encoding.ASCII.GetString(head.Slice(MagicOffset, ExpectedMagic.Length));

        // A non-matching magic is a warning, not a hard failure: the container has changed
        // across patches before and we would rather let the user try than block them.
        return magic == ExpectedMagic
            ? new Verdict(true, true, $"AoE4 replay header recognised (game build {build})", build)
            : new Verdict(true, false, $"unrecognised replay header '{Printable(magic)}' — proceeding anyway", build);
    }

    public static Verdict InspectFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return new Verdict(false, false, "the replay file does not exist");

        Span<byte> head = stackalloc byte[64];
        int read;
        using (var stream = File.OpenRead(path))
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        return Inspect(head[..read], info.Length);
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> head, out string kind)
    {
        var count = Math.Min(head.Length, 64);
        var text = Encoding.ASCII.GetString(head[..count]).TrimStart();
        if (text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            kind = "an HTML page";
            return true;
        }
        if (text.StartsWith('{') || text.StartsWith('['))
        {
            kind = "a JSON response";
            return true;
        }
        kind = "";
        return false;
    }

    private static string Printable(string raw) =>
        new(raw.Select(c => char.IsControl(c) ? '.' : c).ToArray());
}
