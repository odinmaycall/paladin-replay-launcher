using Paladin.Core.Logging;

namespace Paladin.Core.Replay;

/// <summary>Uses a replay already on disk. Never moves or deletes the user's own file.</summary>
public sealed class LocalReplayProvider : IReplayProvider
{
    private readonly PaladinLog _log;

    public LocalReplayProvider(PaladinLog log) => _log = log;

    public string Id => "local";
    public string DisplayName => "Local file";

    public bool CanHandle(ReplayRequest request) =>
        string.Equals(request.Kind, Id, StringComparison.OrdinalIgnoreCase);

    public Task<AcquiredReplay> AcquireAsync(ReplayRequest request, string workingDirectory, CancellationToken ct)
    {
        var path = Path.GetFullPath(request.Value);
        if (!File.Exists(path))
            throw new ReplayAcquisitionException($"No replay file at {path}");

        // A file the user saved straight from a replay site may still be compressed.
        // Decompressing writes a NEW file into the session working directory rather than
        // touching theirs, so their own download is never modified or removed.
        var archive = ReplayArchive.DetectFile(path) == ReplayArchive.ArchiveKind.None
            ? new ReplayArchive.Result(path, ReplayArchive.ArchiveKind.None, new FileInfo(path).Length)
            : DecompressIntoWorkingDirectory(path, workingDirectory);

        var verdict = ReplayValidator.InspectFile(archive.Path);
        _log.Info($"Replay check: {verdict.Reason}");
        if (!verdict.IsUsable)
            throw new ReplayAcquisitionException($"{path} is not a usable replay: {verdict.Reason}");

        _log.Info($"Using local replay {archive.Path} ({archive.Bytes:N0} bytes)");

        return Task.FromResult(new AcquiredReplay
        {
            LocalPath = archive.Path,
            SuggestedFileName = ReplayArchive.StripCompressionExtension(
                request.SuggestedName ?? Path.GetFileName(archive.Path)),
            SizeBytes = archive.Bytes,
            GameBuild = verdict.GameBuild,
            // Only a file this provider created may be cleaned up; never the user's own.
            IsTemporary = archive.WasCompressedAs != ReplayArchive.ArchiveKind.None,
        });
    }

    private ReplayArchive.Result DecompressIntoWorkingDirectory(string source, string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var copy = Path.Combine(workingDirectory, Path.GetFileName(source));
        File.Copy(source, copy, overwrite: true);

        var result = ReplayArchive.EnsureDecompressed(copy);
        _log.Info($"Local replay was {result.WasCompressedAs}; decompressed to {result.Bytes:N0} bytes.");

        if (!string.Equals(result.Path, copy, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(copy); } catch (IOException) { }
        }
        return result;
    }
}
