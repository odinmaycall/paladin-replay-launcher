using System.Net;
using Paladin.Core.Logging;

namespace Paladin.Core.Replay;

/// <summary>
/// Downloads a replay from an http(s) URL the caller supplied. It fetches exactly the
/// URL it was given — it does not crawl, scrape or follow links found in page content.
/// </summary>
public sealed class DirectUrlReplayProvider : IReplayProvider
{
    /// <summary>Refuse anything larger than this; real replays measured 3.9 KB - 4 MB.</summary>
    public const long MaxDownloadBytes = 128L * 1024 * 1024;

    private readonly PaladinLog _log;
    private readonly HttpClient _http;

    public DirectUrlReplayProvider(PaladinLog log, HttpClient? http = null)
    {
        _log = log;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "PaladinReplayLauncher/0.1 (+https://github.com/odinmaycall/paladin-replay-launcher)");
    }

    public string Id => "url";
    public string DisplayName => "Direct URL";

    public bool CanHandle(ReplayRequest request) =>
        string.Equals(request.Kind, Id, StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(request.Value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Tries each candidate in turn. A replay source that legitimately has no replay for
    /// one candidate (Microsoft's per-participant 404) is not an error until every
    /// candidate has been tried.
    /// </summary>
    public async Task<AcquiredReplay> AcquireAsync(ReplayRequest request, string workingDirectory, CancellationToken ct)
    {
        var candidates = request.AllCandidates().ToList();
        var failures = new List<string>();

        for (var i = 0; i < candidates.Count; i++)
        {
            try
            {
                return await AcquireOneAsync(candidates[i], request.SuggestedName, workingDirectory, ct);
            }
            catch (ReplayAcquisitionException ex)
            {
                failures.Add(ex.Message);
                if (i < candidates.Count - 1)
                    _log.Warn($"Candidate {i + 1} of {candidates.Count} failed ({ex.Message}); trying the next.");
            }
        }

        throw new ReplayAcquisitionException(candidates.Count == 1
            ? failures[0]
            : $"None of the {candidates.Count} replay sources worked. " + string.Join(" | ", failures));
    }

    private async Task<AcquiredReplay> AcquireOneAsync(
        string candidate, string? suggestedName, string workingDirectory, CancellationToken ct)
    {
        var request = new ReplayRequest { Kind = Id, Value = candidate, SuggestedName = suggestedName };
        if (!Uri.TryCreate(request.Value, UriKind.Absolute, out var uri))
            throw new ReplayAcquisitionException($"'{request.Value}' is not a valid URL.");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new ReplayAcquisitionException($"Refusing to download over '{uri.Scheme}'. Only http and https are allowed.");

        Directory.CreateDirectory(workingDirectory);
        var fileName = ChooseFileName(uri, request.SuggestedName);
        var target = Path.Combine(workingDirectory, fileName);

        _log.Info($"Downloading replay from {uri} ...");

        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ReplayAcquisitionException($"The replay source refused the request ({(int)response.StatusCode} {response.ReasonPhrase}).");
        if (!response.IsSuccessStatusCode)
            throw new ReplayAcquisitionException($"Replay download failed: {(int)response.StatusCode} {response.ReasonPhrase}");

        var declared = response.Content.Headers.ContentLength;
        if (declared is > MaxDownloadBytes)
            throw new ReplayAcquisitionException($"Refusing a {declared:N0} byte download; the limit is {MaxDownloadBytes:N0} bytes.");

        // Header filename wins over the URL path when the server supplies one.
        if (response.Content.Headers.ContentDisposition?.FileNameStar is { Length: > 0 } starName)
            target = Path.Combine(workingDirectory, Sanitise(starName));
        else if (response.Content.Headers.ContentDisposition?.FileName is { Length: > 0 } dispName)
            target = Path.Combine(workingDirectory, Sanitise(dispName.Trim('"')));

        long written;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            written = await CopyCappedAsync(source, destination, MaxDownloadBytes, ct);
        }

        if (written == 0)
        {
            TryDelete(target);
            throw new ReplayAcquisitionException("The replay source returned an empty response.");
        }

        _log.Info($"Downloaded {written:N0} bytes to {target}");

        // Decompress before validating: Microsoft's own replay endpoint serves gzip, and
        // AoE4 cannot read a compressed replay. Detection is by magic bytes because that
        // endpoint's Content-Type (application/zip) and file name (.gz) disagree.
        var archive = ReplayArchive.EnsureDecompressed(target);
        if (archive.WasCompressedAs != ReplayArchive.ArchiveKind.None)
        {
            _log.Info($"Download was {archive.WasCompressedAs}; decompressed to {archive.Bytes:N0} bytes.");
            TryDelete(target);
        }

        var verdict = ReplayValidator.InspectFile(archive.Path);
        _log.Info($"Replay check: {verdict.Reason}");

        if (!verdict.IsUsable)
        {
            TryDelete(archive.Path);
            throw new ReplayAcquisitionException($"Downloaded file is not a usable replay: {verdict.Reason}");
        }

        return new AcquiredReplay
        {
            LocalPath = archive.Path,
            SuggestedFileName = ReplayArchive.StripCompressionExtension(Path.GetFileName(archive.Path)),
            SizeBytes = archive.Bytes,
            GameBuild = verdict.GameBuild,
            IsTemporary = true,
        };
    }

    private static async Task<long> CopyCappedAsync(Stream source, Stream destination, long cap, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > cap)
                throw new ReplayAcquisitionException($"Download exceeded the {cap:N0} byte limit; aborted.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    /// <summary>Derives a safe local file name from the URL, never trusting it as a path.</summary>
    public static string ChooseFileName(Uri uri, string? suggested)
    {
        if (!string.IsNullOrWhiteSpace(suggested)) return Sanitise(suggested);

        var last = uri.Segments.Length > 0 ? Uri.UnescapeDataString(uri.Segments[^1]) : "";
        last = last.Trim('/');
        return string.IsNullOrWhiteSpace(last) ? "paladin-replay.rec" : Sanitise(last);
    }

    /// <summary>
    /// Strips any directory component and every character Windows rejects, so a hostile
    /// URL cannot steer the download outside the session working directory.
    /// </summary>
    public static string Sanitise(string name)
    {
        name = name.Replace('\\', '/');
        var lastSlash = name.LastIndexOf('/');
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        name = name.Replace("..", "_");
        var cleaned = new string(name.Select(c => IsForbidden(c) ? '_' : c).ToArray()).Trim();

        if (cleaned.Length == 0) cleaned = "paladin-replay.rec";
        if (cleaned.Length > 120) cleaned = cleaned[..120];
        return cleaned;
    }

    private static bool IsForbidden(char c) =>
        c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(c);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }
}
