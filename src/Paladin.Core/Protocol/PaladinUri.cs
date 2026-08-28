using System.Web;
using Paladin.Core.Replay;

namespace Paladin.Core.Protocol;

/// <summary>
/// Parses the paladin:// links the Paladin website will emit.
///
/// Supported forms:
///   paladin://replay?url=https%3A%2F%2Fexample.com%2Fgame.rec
///   paladin://replay?path=C%3A%5Ctemp%5Cgame.rec
///   paladin://replay/&lt;match-id&gt;                  -> provider "aoe4replays" (not yet implemented)
///   paladin://replay?id=&lt;match-id&gt;&amp;source=aoe4replays
///
/// Parsing is deliberately strict and lives in Paladin.Core so it is unit tested
/// without a registered protocol handler. Everything arriving here comes from a
/// browser and is treated as untrusted input.
/// </summary>
public static class PaladinUri
{
    public const string Scheme = "paladin";

    public sealed record ParseResult(bool Ok, ReplayRequest? Request, string? Error)
    {
        public static ParseResult Fail(string error) => new(false, null, error);
        public static ParseResult Success(ReplayRequest request) => new(true, request, null);
    }

    public static bool LooksLikePaladinUri(string argument) =>
        argument.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static ParseResult Parse(string raw)
    {
        raw = raw.Trim().Trim('"');
        if (!LooksLikePaladinUri(raw))
            return ParseResult.Fail($"'{raw}' is not a {Scheme}:// link.");

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return ParseResult.Fail($"'{raw}' is not a well-formed URI.");

        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return ParseResult.Fail($"Unexpected scheme '{uri.Scheme}'.");

        // For paladin://replay/123 the Host is "replay" and AbsolutePath is "/123".
        var action = uri.Host;
        if (string.IsNullOrEmpty(action))
            action = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? "";

        if (!string.Equals(action, "replay", StringComparison.OrdinalIgnoreCase))
            return ParseResult.Fail($"Unsupported paladin action '{action}'. Only 'replay' is implemented.");

        var query = HttpUtility.ParseQueryString(uri.Query);

        // A link may carry several `url` values. HttpUtility joins repeats with commas,
        // so they are split back apart here. Order is priority order.
        var rawUrls = (query["url"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (rawUrls.Count > 0)
        {
            var accepted = new List<string>();
            foreach (var candidate in rawUrls)
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var target) ||
                    (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
                {
                    // One bad candidate poisons the link: a caller mixing a file:// in with
                    // real URLs is either confused or hostile, and neither deserves a guess.
                    return ParseResult.Fail($"Every 'url' must be an http(s) URL, got '{candidate}'.");
                }
                accepted.Add(target.ToString());
            }

            return ParseResult.Success(new ReplayRequest
            {
                Kind = "url",
                Value = accepted[0],
                Fallbacks = accepted.Skip(1).ToList(),
                SuggestedName = NullIfBlank(query["name"]),
            });
        }

        var path = query["path"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            return ParseResult.Success(new ReplayRequest
            {
                Kind = "local",
                Value = path,
                SuggestedName = NullIfBlank(query["name"]),
            });
        }

        var id = query["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            // paladin://replay/<match-id>
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) id = segments[^1];
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            var source = NullIfBlank(query["source"]) ?? "aoe4replays";
            return ParseResult.Success(new ReplayRequest
            {
                Kind = source,
                Value = id,
                SuggestedName = NullIfBlank(query["name"]),
            });
        }

        return ParseResult.Fail("The link carried no 'url', 'path' or match id.");
    }

    /// <summary>Builds a link for the website / test page. Escapes the payload properly.</summary>
    public static string BuildUrlLink(string replayUrl) =>
        $"{Scheme}://replay?url={Uri.EscapeDataString(replayUrl)}";

    /// <summary>
    /// Builds a link carrying several candidate URLs, tried in order. Used where the
    /// caller cannot cheaply tell which source actually holds the replay.
    /// </summary>
    public static string BuildUrlLink(IEnumerable<string> replayUrls) =>
        $"{Scheme}://replay?" + string.Join("&", replayUrls.Select(u => $"url={Uri.EscapeDataString(u)}"));

    public static string BuildIdLink(string matchId, string? source = null) =>
        source is null
            ? $"{Scheme}://replay/{Uri.EscapeDataString(matchId)}"
            : $"{Scheme}://replay?id={Uri.EscapeDataString(matchId)}&source={Uri.EscapeDataString(source)}";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
