using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;

namespace Paladin.Core.Dump;

/// <summary>The upload's outcome, one per answer the Worker can give (§717 §4.3) plus the two ways of not getting one.</summary>
public enum DumpUploadOutcome
{
    /// <summary>200 stored: the map is served from now on.</summary>
    Stored,
    /// <summary>200 already: someone's verified dump holds the slot; nothing was stored.</summary>
    Already,
    /// <summary>409 banked: the owner's static file exists.</summary>
    Banked,
    /// <summary>404 no_sidecar: Paladin has nothing to check the rows against.</summary>
    NoSidecar,
    /// <summary>400 with a code from §5.2 (wrong_replay, seed_mismatch, incomplete, ...).</summary>
    Rejected,
    /// <summary>413, or the local 1 MiB cap before anything was sent.</summary>
    TooLarge,
    /// <summary>415.</summary>
    UnsupportedMedia,
    /// <summary>429.</summary>
    RateLimited,
    /// <summary>405.</summary>
    MethodNotAllowed,
    /// <summary>503 with the Worker's own uploads_closed code: the KV binding is absent, and no retry can help.</summary>
    UploadsClosed,
    /// <summary>Any other 5xx, a bare 503 included: the answer of something in front of the Worker, worth a retry.</summary>
    ServerError,
    /// <summary>No HTTP answer: DNS, connection, TLS, a cut mid-answer or the timeout.</summary>
    Unreachable,
    /// <summary>An answer this version does not understand.</summary>
    Unexpected,
}

/// <param name="Attempts">How many POSTs were made: 1, or up to <see cref="DumpUploader.MaxAttempts"/> when network failures or 5xx answers were retried.</param>
/// <param name="LandedEarlier">
/// The answer was "already" on a retry, and an earlier attempt of this same run was
/// sent but never answered (the timeout, or the line cut after the send). That earlier
/// POST is the one the Worker stored, so this run's upload landed and the console gets
/// the success line, not F10.
/// </param>
public sealed record DumpUploadResult(
    DumpUploadOutcome Outcome, int? HttpCode, DumpUploadResponse? Response, string? Body, string? Error,
    int Attempts = 1, bool LandedEarlier = false)
{
    /// <summary>Stored, or "already" answered to the retry of an attempt that was sent and never answered.</summary>
    public bool Accepted => Outcome == DumpUploadOutcome.Stored || (Outcome == DumpUploadOutcome.Already && LandedEarlier);

    /// <summary>F11: the rows are kept and `--dump-upload` can send them later.</summary>
    public bool RetryLater => Outcome is DumpUploadOutcome.Unreachable or DumpUploadOutcome.ServerError or DumpUploadOutcome.UploadsClosed;

    /// <summary>The error code the Worker gave, or the one implied by the HTTP code.</summary>
    public string? Code => Response?.Error ?? Outcome switch
    {
        DumpUploadOutcome.Already => "already",
        DumpUploadOutcome.Banked => "banked",
        DumpUploadOutcome.NoSidecar => "no_sidecar",
        DumpUploadOutcome.TooLarge => "too_large",
        DumpUploadOutcome.UnsupportedMedia => "unsupported_media_type",
        DumpUploadOutcome.RateLimited => "rate_limited",
        DumpUploadOutcome.MethodNotAllowed => "method_not_allowed",
        DumpUploadOutcome.UploadsClosed => "uploads_closed",
        _ => null,
    };

    /// <summary>The plain words for the F10 line: the code's sentence, with the Worker's own detail only (never the HTTP reason phrase).</summary>
    public string PlainWords => DumpUploadResponse.PlainWords(Code, Response?.Detail);
}

public sealed record DumpPreflight(bool Reachable, DumpStatus? Status, int? HttpCode, string? Error)
{
    /// <summary>The game already has a world layer; a dump would be refused.</summary>
    public bool HasWorld => Status?.HasWorld == true;
}

/// <summary>
/// The two requests a dump makes to paladin.odinmaycall.com (§717 §3.2, §4): the
/// pre-flight GET, once, and the POST with the design's retry policy — up to
/// <see cref="MaxAttempts"/> attempts on a network failure or a 5xx, <see cref="RetryDelaySeconds"/>
/// apart, never on a 4xx and never on uploads_closed. A retry cannot double-post: the
/// Worker holds one slot per game and answers a second POST for a stored game with
/// "already" without writing (§5.2 step 7, §5.5), so the retry costs no KV write on the
/// free tier (D4). No cookies, no auth. The target origin is the configured base URL's
/// and never comes from a link.
/// </summary>
public sealed class DumpUploader
{
    public const string DefaultBaseUrl = "https://paladin.odinmaycall.com/api/world/";
    public const int UploadTimeoutSeconds = 60;
    public const int PreflightTimeoutSeconds = 15;
    /// <summary>One POST plus the design's two retries (§3.2).</summary>
    public const int MaxAttempts = 3;
    /// <summary>The back-off between attempts (§3.2).</summary>
    public const int RetryDelaySeconds = 5;
    /// <summary>Header carrying the launcher version beside the User-Agent, so the Worker can read it without parsing.</summary>
    public const string LauncherHeader = "X-Paladin-Launcher";
    public const string ProjectUrl = "https://github.com/odinmaycall/paladin-replay-launcher";
    /// <summary>How much of an answer's body is read and kept; the Worker's answers are a few hundred bytes.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _version;
    private readonly PaladinLog _log;
    private readonly TimeSpan _uploadTimeout;
    private readonly TimeSpan _preflightTimeout;
    private readonly TimeSpan _retryDelay;

    /// <param name="http">Injected by the tests with an in-process handler; production builds its own.</param>
    /// <param name="uploadTimeout">Defaults to <see cref="UploadTimeoutSeconds"/>; a test shortens it.</param>
    /// <param name="preflightTimeout">Defaults to <see cref="PreflightTimeoutSeconds"/>.</param>
    /// <param name="retryDelay">Defaults to <see cref="RetryDelaySeconds"/>; a test sets it to zero.</param>
    public DumpUploader(
        string baseUrl, string launcherVersion, PaladinLog log, HttpClient? http = null,
        TimeSpan? uploadTimeout = null, TimeSpan? preflightTimeout = null, TimeSpan? retryDelay = null)
    {
        _base = BaseUri(baseUrl);
        _version = launcherVersion;
        _log = log;
        _uploadTimeout = uploadTimeout ?? TimeSpan.FromSeconds(UploadTimeoutSeconds);
        _preflightTimeout = preflightTimeout ?? TimeSpan.FromSeconds(PreflightTimeoutSeconds);
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(RetryDelaySeconds);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            // Per-request timeouts below are the real limits; this only stops a runaway.
            Timeout = TimeSpan.FromSeconds(UploadTimeoutSeconds + 5),
        };
    }

    public Uri BaseUrl => _base;
    public TimeSpan RetryDelay => _retryDelay;

    public static string UserAgentFor(string version) => $"PaladinReplayLauncher/{version} (+{ProjectUrl})";

    /// <summary>The base URL, validated: absolute, http(s), ending in "/" so the game id appends as a path segment.</summary>
    public static Uri BaseUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"DumpUploadBaseUrl must be an http(s) URL, got '{baseUrl}'.", nameof(baseUrl));
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("DumpUploadBaseUrl must carry no query or fragment.", nameof(baseUrl));
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.GetLeftPart(UriPartial.Path) + "/");
    }

    /// <summary>https://paladin.odinmaycall.com/api/world/246737201 — the id is digits only, so it cannot steer the path.</summary>
    public static Uri TargetFor(string baseUrl, long gameId)
    {
        if (!PaladinUri.TryParseGameId(gameId.ToString(System.Globalization.CultureInfo.InvariantCulture), out _))
            throw new ArgumentOutOfRangeException(nameof(gameId), $"'{gameId}' is not a game id.");
        return new Uri(BaseUri(baseUrl), gameId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public Uri TargetFor(long gameId) => TargetFor(_base.ToString(), gameId);

    /// <summary>GET /api/world/&lt;id&gt;, once. A failure here is a warning, never a stop (§3.3 step 0).</summary>
    public async Task<DumpPreflight> CheckAsync(long gameId, CancellationToken ct)
    {
        var target = TargetFor(gameId);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        AddHeaders(request);

        _log.Debug($"Pre-flight GET {target}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_preflightTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadCappedAsync(response, timeout.Token);
            var code = (int)response.StatusCode;

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _log.Warn($"Pre-flight answered {code}");
                return new DumpPreflight(true, null, code, $"{code} {response.ReasonPhrase}");
            }

            var status = DumpStatus.TryParse(body);
            if (status is null)
            {
                _log.Warn("Pre-flight answered 200 with a body that is not the status shape");
                return new DumpPreflight(true, null, code, "the answer was not JSON");
            }
            _log.Info($"Pre-flight: status {status.Status ?? "?"}, n {status.N?.ToString() ?? "?"}");
            return new DumpPreflight(true, status, code, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"Pre-flight: no answer within {_preflightTimeout.TotalSeconds:0.##} s");
            return new DumpPreflight(false, null, null, $"no answer within {_preflightTimeout.TotalSeconds:0.##} s");
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"Pre-flight: {ex.Message}");
            return new DumpPreflight(false, null, null, ex.Message);
        }
        catch (IOException ex)
        {
            // The headers arrived and the line was cut under the body (HttpIOException derives from this).
            _log.Warn($"Pre-flight: the answer was cut short: {ex.Message}");
            return new DumpPreflight(false, null, null, $"the answer was cut short: {ex.Message}");
        }
    }

    /// <summary>
    /// POST /api/world/&lt;id&gt; with the envelope as application/json: up to
    /// <see cref="MaxAttempts"/> attempts on Unreachable or ServerError, <see cref="RetryDelaySeconds"/>
    /// apart; any other answer ends it. Ctrl+C during the wait propagates as cancellation.
    /// </summary>
    public async Task<DumpUploadResult> UploadAsync(DumpEnvelope envelope, CancellationToken ct)
    {
        var bytes = envelope.ToUtf8();
        if (bytes.LongLength > DumpEnvelope.MaxBytes)
        {
            _log.Warn($"Dump envelope is {bytes.LongLength:N0} bytes, over the {DumpEnvelope.MaxBytes:N0} byte cap; not sent");
            return new DumpUploadResult(DumpUploadOutcome.TooLarge, null, null, null,
                $"the envelope is {bytes.LongLength:N0} bytes; the limit is {DumpEnvelope.MaxBytes:N0}");
        }

        var target = TargetFor(envelope.GameId);
        var sentUnanswered = false;
        for (var attempt = 1; ; attempt++)
        {
            var (result, sent) = await PostOnceAsync(target, bytes, attempt, ct);
            sentUnanswered |= sent && result.Outcome == DumpUploadOutcome.Unreachable;

            if (result.Outcome == DumpUploadOutcome.Already && sentUnanswered)
            {
                _log.Info($"Upload answered already on attempt {attempt}: an earlier attempt of this run was sent and never answered, so it is the one that landed");
                return result with { Attempts = attempt, LandedEarlier = true };
            }
            if (result.Outcome is not (DumpUploadOutcome.Unreachable or DumpUploadOutcome.ServerError) || attempt >= MaxAttempts)
                return result with { Attempts = attempt };

            _log.Warn($"Upload attempt {attempt} of {MaxAttempts}: {result.Error}; retrying in {_retryDelay.TotalSeconds:0.##} s");
            await Task.Delay(_retryDelay, ct);
        }
    }

    /// <summary>One POST. <c>sent</c> is whether the bytes may have reached the Worker: false only when the failure came before any send.</summary>
    private async Task<(DumpUploadResult Result, bool Sent)> PostOnceAsync(Uri target, byte[] bytes, int attempt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(bytes),
        };
        // Exactly "application/json": the Worker checks the media type, and a charset suffix
        // is one more thing for it to have to allow.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        AddHeaders(request);

        _log.Info($"Uploading {bytes.LongLength:N0} bytes to {target.Host}{(attempt > 1 ? $" (attempt {attempt} of {MaxAttempts})" : "")}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_uploadTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await ReadCappedAsync(response, timeout.Token);
            var result = Classify((int)response.StatusCode, response.ReasonPhrase, body);
            _log.Info($"Upload answered {result.HttpCode}: {result.Outcome}{(result.Error is null ? "" : $" ({result.Error})")}");
            return (result, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.Warn($"Upload: no answer within {_uploadTimeout.TotalSeconds:0.##} s");
            return (new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, $"no answer within {_uploadTimeout.TotalSeconds:0.##} s"), true);
        }
        catch (HttpRequestException ex)
        {
            _log.Warn($"Upload: {ex.Message}");
            return (new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, ex.Message), !FailedBeforeSending(ex));
        }
        catch (IOException ex)
        {
            // The headers arrived and the line was cut under the body (HttpIOException derives from this).
            _log.Warn($"Upload: the answer was cut short: {ex.Message}");
            return (new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, $"the answer was cut short: {ex.Message}"), true);
        }
    }

    /// <summary>DNS, connect, TLS, proxy and version failures happen before a byte of the request leaves; anything else may have reached the Worker.</summary>
    public static bool FailedBeforeSending(HttpRequestException ex) => ex.HttpRequestError is
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
        or HttpRequestError.ProxyTunnelError or HttpRequestError.VersionNegotiationError;

    /// <summary>The response vocabulary of §4.3 mapped to outcomes. Pure, so every branch is tested without a socket.</summary>
    public static DumpUploadResult Classify(int code, string? reason, string? body)
    {
        var parsed = DumpUploadResponse.TryParse(body);
        var kept = body is { Length: > 512 } ? body[..512] : body;

        var outcome = code switch
        {
            200 => parsed?.Status switch
            {
                "stored" => DumpUploadOutcome.Stored,
                "already" => DumpUploadOutcome.Already,
                _ => DumpUploadOutcome.Unexpected,
            },
            400 => DumpUploadOutcome.Rejected,
            404 => DumpUploadOutcome.NoSidecar,
            405 => DumpUploadOutcome.MethodNotAllowed,
            409 => DumpUploadOutcome.Banked,
            413 => DumpUploadOutcome.TooLarge,
            415 => DumpUploadOutcome.UnsupportedMedia,
            429 => DumpUploadOutcome.RateLimited,
            // Only the Worker's own code closes uploads; a bare 503 is Cloudflare's, and transient.
            503 when parsed?.Error == "uploads_closed" => DumpUploadOutcome.UploadsClosed,
            >= 500 => DumpUploadOutcome.ServerError,
            _ => DumpUploadOutcome.Unexpected,
        };

        var error = outcome switch
        {
            DumpUploadOutcome.Stored or DumpUploadOutcome.Already => null,
            DumpUploadOutcome.Rejected => parsed?.Detail ?? parsed?.Error ?? $"{code} {reason}",
            _ => parsed?.Detail ?? $"{code} {reason}".Trim(),
        };

        return new DumpUploadResult(outcome, code, parsed, kept, error);
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentFor(_version));
        request.Headers.TryAddWithoutValidation(LauncherHeader, _version);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var kept = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            var room = MaxBodyBytes - (int)kept.Length;
            if (room <= 0) break;
            kept.Write(buffer, 0, Math.Min(read, room));
        }
        return Encoding.UTF8.GetString(kept.GetBuffer(), 0, (int)kept.Length);
    }
}
