using System.Net.Http.Headers;
using System.IO.Compression;
using Paladin.Core.Logging;

namespace Paladin.Core.Deep;

/// <summary>
/// §879 — RETAINING THE REPLAY AT CAPTURE TIME, which is the only moment it is certainly still there.
///
/// WHY THIS EXISTS. A Deep sampler artifact on its own is not the trusted result the owner wants: the
/// build order's clocks, its Builders column and its landmark placements all come from the REPLAY's
/// order stream, not from the capture. Paladin can only parse a replay it holds, and Microsoft stops
/// serving them about three months after the game — so a capture made today becomes permanently
/// half-evidenced unless the replay is kept now.
///
/// WHY THE REPLAY AND NOT THE PARSE. The reader's browser already parses the replay for its own page,
/// and persisting THAT would mean serving one reader's parse to every other reader under a trusted
/// label. Sending the replay instead means Paladin always parses with its own parser: no trust
/// surface, and the evidence stays RE-PARSEABLE, so a future parser improvement reaches every
/// captured game without anyone replaying anything.
///
/// IT IS SENT GZIPPED, which is the format Paladin's own bank already stores (`.rec.gz`), so the
/// bytes that arrive are the bytes that get kept. Measured over 3,408 banked replays: a median of
/// 289 KiB compressed and a worst case of 1.51 MiB, against the Worker's 4 MiB replay cap.
///
/// FAILING IS NOT FATAL, AND THAT IS DELIBERATE. The capture has already landed by the time this
/// runs; a replay that will not upload leaves the game recoverable — the package reports `partial`,
/// and the reader (or the owner) can complete it later without replaying anything.
/// </summary>
public sealed class ReplayUploader
{
    public const int UploadTimeoutSeconds = 120;
    /// <summary>The Worker's own cap (§879). Checked here so a doomed body is never put on the wire.</summary>
    public const long WireCapBytes = 4L * 1024 * 1024;

    private readonly Uri _base;
    private readonly string _version;
    private readonly PaladinLog _log;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public ReplayUploader(string? baseUrl, string launcherVersion, PaladinLog log, HttpClient? http = null, TimeSpan? timeout = null)
    {
        // §871's lesson, reused rather than re-derived: the configured address is the WORLD DUMP's and
        // already ends in /api/world/, so only its ORIGIN may be built on.
        _base = DeepUploader.OriginOf(baseUrl);
        _version = launcherVersion;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(UploadTimeoutSeconds);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(UploadTimeoutSeconds + 5) };
    }

    public string Host => _base.Authority;

    public Uri TargetFor(long gameId) => new(_base, $"api/replay/{gameId}");

    /// <summary>
    /// §879 — gzip a replay for the wire. Pure and static so the size check below is testable without
    /// a socket, and so the compression level is stated once rather than assumed.
    /// </summary>
    public static byte[] Compress(byte[] replayBytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(replayBytes, 0, replayBytes.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Send it. Returns a short outcome for the console rather than a rich result: nothing downstream
    /// branches on this, because a failed retention never fails a capture that already succeeded.
    /// </summary>
    public async Task<ReplayUploadResult> UploadAsync(long gameId, string replayPath, CancellationToken ct)
    {
        byte[] raw;
        try
        {
            raw = await File.ReadAllBytesAsync(replayPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"Could not read the replay to retain it: {ex.Message}");
            return new ReplayUploadResult(false, 0, 0, $"the replay could not be read ({ex.Message})");
        }

        var body = Compress(raw);
        if (body.LongLength > WireCapBytes)
        {
            // Saying so here is better than a 413 after a two-minute upload.
            return new ReplayUploadResult(false, body.Length, raw.Length, $"the compressed replay is {body.Length / 1024} KiB, over Paladin's {WireCapBytes / 1024} KiB limit");
        }

        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
            using var request = new HttpRequestMessage(HttpMethod.Post, TargetFor(gameId)) { Content = content };
            request.Headers.TryAddWithoutValidation("X-Paladin-Launcher", _version);
            request.Headers.UserAgent.ParseAdd($"PaladinReplayLauncher/{_version} (+https://github.com/odinmaycall/paladin-replay-launcher)");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                _log.Warn($"Retaining the replay was refused: HTTP {(int)response.StatusCode} {text}");
                return new ReplayUploadResult(false, body.Length, raw.Length, $"Paladin refused it (HTTP {(int)response.StatusCode})");
            }
            // "parsed" means the whole package is complete; "retained" means the bytes are safe and the
            // parse can be redone later. Both are successes from here.
            var parsed = text.Contains("\"status\":\"parsed\"", StringComparison.Ordinal);
            _log.Info($"Replay retained for game {gameId}: {body.Length} bytes on the wire, parsed={parsed}");
            return new ReplayUploadResult(true, body.Length, raw.Length, null, parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.Warn($"Could not reach {Host} to retain the replay: {ex.Message}");
            return new ReplayUploadResult(false, body.Length, raw.Length, $"could not reach {Host} ({ex.Message})");
        }
    }
}

/// <param name="Parsed">True when Paladin parsed it on the spot, so the package is already complete.</param>
public sealed record ReplayUploadResult(bool Ok, int WireBytes, int RawBytes, string? Error, bool Parsed = false);
