using System.Net.Http.Headers;
using System.Text.Json;
using Paladin.Core.Dump;
using Paladin.Core.Logging;

namespace Paladin.Core.Deep;

/// <param name="Status">"none", "owner", "user" or "unknown" — the Worker's own vocabulary.</param>
/// <param name="Gzip">The Worker announced it will inflate a compressed body. False on an older Worker.</param>
/// <param name="RequiredSeconds">
/// §876 — the clock a capture of THIS match must reach: the 15:00 window, or the end of a game that
/// finished sooner. 22% of the tournament set and 18% of the indexed ladder end before 15:00, and this
/// launcher hard-coded 900 — so a reader who captured a 10:15 game end to end was told their complete
/// capture had failed. Zero means the Worker did not say (an older Paladin, or one we could not reach),
/// and the caller then uses the window, exactly as this launcher always did.
/// </param>
public sealed record DeepPreflight(string Status, bool Match, bool Orders, bool Gzip, long WireBytes, long TextBytes, string? Error = null, int RequiredSeconds = 0)
{
    public bool Reachable => Error is null;
    public bool AlreadyHeld => Status is "owner" or "user";

    /// <summary>§876 — the clock to capture to: what the Worker said, or the window when it said nothing.</summary>
    public int WindowOr(int fallback) => RequiredSeconds > 0 ? RequiredSeconds : fallback;
}

/// <summary>
/// §867 — sending a Deep capture to Paladin: `POST /api/deep/&lt;id&gt;`, gzipped.
///
/// A deliberately thin class. The response vocabulary, the retry shape and the headers are the world
/// dump's, reused rather than re-derived — <see cref="DumpUploader.Classify"/> is pure and already maps
/// every answer the Worker gives, and a second interpretation of "409 banked" is exactly the kind of
/// divergence that ends with two halves of Paladin disagreeing about what happened.
///
/// WHAT IS GENUINELY DIFFERENT IS THE BODY. It goes up gzipped, because the production cadence puts the
/// largest real captures at 1,277 KiB of JSON against a 1 MiB cap. The Worker sniffs the body's first
/// two bytes rather than trusting Content-Encoding, so this sets the header for correctness and does not
/// depend on it being honoured by anything in between.
/// </summary>
public sealed class DeepUploader
{
    private readonly Uri _base;
    private readonly string _version;
    private readonly PaladinLog _log;
    private readonly HttpClient _http;
    private readonly TimeSpan _uploadTimeout;
    private readonly TimeSpan _preflightTimeout;

    public const int UploadTimeoutSeconds = 120;
    public const int PreflightTimeoutSeconds = 15;
    public const int MaxAttempts = 3;

    public DeepUploader(string baseUrl, string launcherVersion, PaladinLog log, HttpClient? http = null,
        TimeSpan? uploadTimeout = null, TimeSpan? preflightTimeout = null)
    {
        _base = OriginOf(baseUrl);
        _version = launcherVersion;
        _log = log;
        _uploadTimeout = uploadTimeout ?? TimeSpan.FromSeconds(UploadTimeoutSeconds);
        _preflightTimeout = preflightTimeout ?? TimeSpan.FromSeconds(PreflightTimeoutSeconds);
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(UploadTimeoutSeconds + 5),
        };
    }

    public string Host => _base.Authority;

    public Uri TargetFor(long gameId) => new(_base, $"api/deep/{gameId}");

    /// <summary>
    /// §867 — THE ORIGIN OF THE CONFIGURED ADDRESS, not the address itself.
    ///
    /// This is a bug that got as far as launching a game before it was caught. `DumpUploadBaseUrl` is
    /// the WORLD DUMP's endpoint — it already ends in `/api/world/` — so appending `api/deep/&lt;id&gt;` to
    /// it produced `/api/world/api/deep/&lt;id&gt;`, which the site answers with the SPA fallback: HTTP 200,
    /// `text/html`, and a pre-flight that could not parse it. The launcher then shrugged, said it could
    /// not reach Paladin, and went ahead and captured a game Paladin already had.
    ///
    /// Taking the origin keeps the one property that matters: a run pointed at localhost:8787 still
    /// talks to localhost, so a test deploy is never silently sent to production.
    /// </summary>
    public static Uri OriginOf(string? configuredBaseUrl)
    {
        var text = string.IsNullOrWhiteSpace(configuredBaseUrl) ? "https://paladin.odinmaycall.com" : configuredBaseUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed))
            return new Uri("https://paladin.odinmaycall.com/", UriKind.Absolute);
        return new Uri($"{parsed.Scheme}://{parsed.Authority}/", UriKind.Absolute);
    }

    /// <summary>
    /// What Paladin already knows about this game — asked BEFORE a replay is launched, because a capture
    /// costs fifteen minutes of playback and a game whose slot is already held costs fifteen minutes for
    /// nothing.
    /// </summary>
    public async Task<DeepPreflight> CheckAsync(long gameId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TargetFor(gameId));
        AddHeaders(request);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_preflightTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ParsePreflight(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.Warn($"Deep pre-flight could not reach {Host}: {ex.Message}");
            return new DeepPreflight("unknown", false, false, false, 0, 0, ex.Message);
        }
    }

    /// <summary>Pure, so every branch is tested without a socket.</summary>
    public static DeepPreflight ParsePreflight(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new DeepPreflight("unknown", false, false, false, 0, 0, "empty answer");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            bool Bool(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            long Num(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

            var status = Str("status");
            if (status.Length == 0) return new DeepPreflight("unknown", false, false, false, 0, 0, "no status in the answer");
            // §876 — `required` is this match's own clock; `window` is the sheet's 15:00 and is what a
            // Paladin older than §876 publishes. Either is better than a number compiled in here, and
            // a Worker that sends neither leaves this at zero, which means "use the window".
            var required = Num("required");
            if (required <= 0) required = Num("window");
            return new DeepPreflight(status, Bool("match"), Bool("orders"), Bool("gzip"), Num("wireBytes"), Num("textBytes"), null, (int)Math.Clamp(required, 0, int.MaxValue));
        }
        catch (JsonException ex)
        {
            return new DeepPreflight("unknown", false, false, false, 0, 0, $"the answer was not JSON: {ex.Message}");
        }
    }

    /// <summary>Send it. Retries only what is worth retrying, using the dump's own classification.</summary>
    public async Task<DumpUploadResult> UploadAsync(DeepEnvelope envelope, CancellationToken ct)
    {
        var bytes = envelope.ToGzip();
        var target = TargetFor(envelope.GameId);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = new ByteArrayContent(bytes),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            // The Worker decides by the body's magic bytes, so this is a correctness signal rather than
            // something it depends on — a proxy that inflates on the way in still produces a valid send.
            request.Content.Headers.ContentEncoding.Add("gzip");
            AddHeaders(request);

            _log.Info($"Uploading {bytes.LongLength:N0} gzipped bytes ({envelope.JsonBytes:N0} raw) to {target.Host}{(attempt > 1 ? $" (attempt {attempt} of {MaxAttempts})" : "")}");
            DumpUploadResult result;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_uploadTimeout);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                result = DumpUploader.Classify((int)response.StatusCode, response.ReasonPhrase, body);
                _log.Info($"Deep upload answered {result.HttpCode}: {result.Outcome}{(result.Error is null ? "" : $" ({result.Error})")}");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                _log.Warn($"Deep upload: {ex.Message}");
                result = new DumpUploadResult(DumpUploadOutcome.Unreachable, null, null, null, ex.Message);
            }

            if (result.Outcome is not (DumpUploadOutcome.Unreachable or DumpUploadOutcome.ServerError) || attempt >= MaxAttempts)
                return result with { Attempts = attempt };

            _log.Warn($"Deep upload attempt {attempt} of {MaxAttempts} failed; retrying");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", DumpUploader.UserAgentFor(_version));
        request.Headers.TryAddWithoutValidation(DumpUploader.LauncherHeader, _version);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }
}
