using System.IO.Compression;

namespace Paladin.Core.Replay;

/// <summary>
/// Replay downloads do not always arrive as a raw replay.
///
/// Measured against Microsoft's own live endpoint
/// (api.ageofempires.com/.../GetMatchReplay): the response is **gzip**, magic
/// <c>1f 8b 08</c>, 447 KB compressed to 1.79 MB of real replay with <c>AOE4_RE</c> at
/// offset 4. Note that the server describes it as <c>Content-Type: application/zip</c>
/// and names it <c>.gz</c> — the header and the extension disagree, and only one of them
/// is right. So detection is by magic bytes, never by content type or file name.
///
/// AoE4 cannot read a compressed replay, so this must happen before the file is placed
/// in the playback folder.
/// </summary>
public static class ReplayArchive
{
    /// <summary>Guards against a decompression bomb. Real replays measured 4 KB - 4 MB.</summary>
    public const long MaxDecompressedBytes = 256L * 1024 * 1024;

    public enum ArchiveKind { None, Gzip, Zip }

    public static ArchiveKind Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0x1f && head[1] == 0x8b && head[2] == 0x08)
            return ArchiveKind.Gzip;

        // "PK\x03\x04" — a real zip local file header.
        if (head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4b && head[2] == 0x03 && head[3] == 0x04)
            return ArchiveKind.Zip;

        return ArchiveKind.None;
    }

    public static ArchiveKind DetectFile(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Detect(head[..read]);
    }

    public sealed record Result(string Path, ArchiveKind WasCompressedAs, long Bytes);

    /// <summary>
    /// Returns a path to an uncompressed replay. When the input was not compressed the
    /// original path is returned untouched; otherwise the content is written alongside it
    /// and that new path is returned. Never deletes the caller's input.
    /// </summary>
    public static Result EnsureDecompressed(string path)
    {
        var kind = DetectFile(path);
        if (kind == ArchiveKind.None)
            return new Result(path, ArchiveKind.None, new FileInfo(path).Length);

        var target = BuildTargetPath(path);

        try
        {
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (kind == ArchiveKind.Gzip)
                {
                    using var input = File.OpenRead(path);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    CopyCapped(gzip, output);
                }
                else
                {
                    using var zip = ZipFile.OpenRead(path);
                    // Replay archives observed in the wild hold exactly one file; taking the
                    // largest entry is the safe generalisation if that ever changes.
                    var entry = zip.Entries
                        .Where(e => !string.IsNullOrEmpty(e.Name))
                        .OrderByDescending(e => e.Length)
                        .FirstOrDefault()
                        ?? throw new ReplayAcquisitionException("The downloaded zip contained no files.");

                    using var entryStream = entry.Open();
                    CopyCapped(entryStream, output);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            TryDelete(target);
            throw new ReplayAcquisitionException($"The download looked compressed but could not be read: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(target);
            throw new ReplayAcquisitionException($"Could not decompress the replay: {ex.Message}", ex);
        }

        // A truncated gzip can present a valid 10-byte header and then decompress to
        // nothing at all without throwing. An empty result is never a usable replay, so
        // treat it as the failure it is rather than placing a 0-byte file for AoE4.
        var produced = new FileInfo(target).Length;
        if (produced == 0)
        {
            TryDelete(target);
            throw new ReplayAcquisitionException(
                $"The download was {kind} but contained no data once decompressed — it is truncated or corrupt.");
        }

        return new Result(target, kind, produced);
    }

    /// <summary>
    /// Drops a compression extension so the placed replay is not called ".gz".
    /// Microsoft names its download AgeIV_Replay_226730901.gz, and AoE4 expects
    /// AgeIV_Replay_226730901.
    /// </summary>
    public static string StripCompressionExtension(string fileName)
    {
        foreach (var ext in new[] { ".gz", ".gzip", ".zip" })
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return fileName[..^ext.Length];
        }
        return fileName;
    }

    private static string BuildTargetPath(string source)
    {
        var directory = Path.GetDirectoryName(source) ?? "";
        var stripped = StripCompressionExtension(Path.GetFileName(source));
        if (string.Equals(stripped, Path.GetFileName(source), StringComparison.Ordinal))
            stripped += ".replay";
        return Path.Combine(directory, stripped);
    }

    private static void CopyCapped(Stream source, Stream destination)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDecompressedBytes)
                throw new ReplayAcquisitionException($"Decompressed replay exceeded {MaxDecompressedBytes:N0} bytes; aborted.");
            destination.Write(buffer, 0, read);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
