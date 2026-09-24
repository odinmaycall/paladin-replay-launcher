using System.Globalization;
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
///   paladin://dump?game=&lt;id&gt;&amp;url=...&amp;url=...        -> "Dump this game" (§717): the replay
///                                                    as for replay, plus the game id the rows
///                                                    are sent under. The upload target never
///                                                    travels in the link; it is config.
///
/// Parsing is deliberately strict and lives in Paladin.Core so it is unit tested
/// without a registered protocol handler. Everything arriving here comes from a
/// browser and is treated as untrusted input.
/// </summary>
public static class PaladinUri
{
    public const string Scheme = "paladin";
    public const string ReplayAction = "replay";
    public const string DumpAction = "dump";

    /// <summary>
    /// §867 — "Deep Capture": paladin://deep?game=&lt;id&gt;&amp;url=... It carries exactly what a dump link
    /// carries, because it needs exactly the same things — the replay to play and the game the result is
    /// stored under. What differs is entirely on this side of the link.
    /// </summary>
    public const string DeepAction = "deep";

    /// <summary>A Paladin game id is a positive number of at most this many digits (the Worker's route is \d{1,15}).</summary>
    public const int MaxGameIdDigits = 15;

    /// <summary>"Dump, then watch": the page's &amp;then=watch on a dump link (§717 §3.1).</summary>
    public const string ThenWatchValue = "watch";

    /// <param name="Action">"replay" or "dump" when Ok.</param>
    /// <param name="GameId">The game the rows are sent under; only a dump link carries one.</param>
    /// <param name="ThenWatch">
    /// The link carried then=watch: once the dump has been sent, the replay is launched
    /// again as an ordinary watch session. Any other then= value is ignored rather than
    /// refused — an unknown follow-on is not a reason to throw away a good dump link.
    /// </param>
    public sealed record ParseResult(
        bool Ok, ReplayRequest? Request, string? Error, string? Action = null, long? GameId = null, bool ThenWatch = false)
    {
        public static ParseResult Fail(string error) => new(false, null, error);
        public static ParseResult Success(ReplayRequest request, string action = ReplayAction, long? gameId = null, bool thenWatch = false) =>
            new(true, request, null, action, gameId, thenWatch);

        public bool IsDump => Ok && Action == DumpAction;

        /// <summary>§867 — a Deep Capture link.</summary>
        public bool IsDeep => Ok && Action == DeepAction;

        /// <summary>Either of the two forms that name a game and capture it.</summary>
        public bool IsCapture => IsDump || IsDeep;
    }

    public static bool LooksLikePaladinUri(string argument) =>
        argument.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase) ||
        argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The link's action ("replay", "dump", ...) lower-cased, or null when the argument
    /// is not a well-formed paladin link. Lets the command line route a bare link to
    /// the right command before the full parse.
    /// </summary>
    public static string? ActionOf(string raw)
    {
        raw = raw.Trim().Trim('"');
        if (!LooksLikePaladinUri(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        var action = uri.Host;
        if (string.IsNullOrEmpty(action))
            action = uri.AbsolutePath.Trim('/').Split('/').FirstOrDefault() ?? "";
        return action.Length == 0 ? null : action.ToLowerInvariant();
    }

    /// <summary>Digits only, 1 to <see cref="MaxGameIdDigits"/> of them, greater than zero. No sign, no spaces, no separators.</summary>
    public static bool TryParseGameId(string? text, out long gameId)
    {
        gameId = 0;
        if (string.IsNullOrEmpty(text) || text.Length > MaxGameIdDigits) return false;
        foreach (var c in text)
            if (c is < '0' or > '9') return false;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out gameId) && gameId > 0;
    }

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

        var isReplay = string.Equals(action, ReplayAction, StringComparison.OrdinalIgnoreCase);
        var isDump = string.Equals(action, DumpAction, StringComparison.OrdinalIgnoreCase);
        var isDeep = string.Equals(action, DeepAction, StringComparison.OrdinalIgnoreCase);
        if (!isReplay && !isDump && !isDeep)
            return ParseResult.Fail($"Unsupported paladin action '{action}'. Only '{ReplayAction}', '{DumpAction}' and '{DeepAction}' are implemented.");

        var actionName = isDeep ? DeepAction : isDump ? DumpAction : ReplayAction;
        var query = HttpUtility.ParseQueryString(uri.Query);

        // A dump is keyed by the game id the rows are sent under. It is digits or nothing:
        // a repeated game= (joined with a comma by ParseQueryString) or anything else is refused.
        long? gameId = null;
        var thenWatch = false;
        if (isDump || isDeep)
        {
            var game = query["game"];
            if (!TryParseGameId(game, out var id))
                return ParseResult.Fail(game is null
                    ? $"A {actionName} link needs game=<id>."
                    : $"'{game}' is not a game id (1 to {MaxGameIdDigits} digits).");
            gameId = id;

            // "Dump, then watch" is the same dump with a watch after it. Only this one value
            // is understood; anything else in then= is ignored, never refused.
            thenWatch = string.Equals(query["then"], ThenWatchValue, StringComparison.OrdinalIgnoreCase);
        }

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
            }, actionName, gameId, thenWatch);
        }

        var path = query["path"];
        if (!string.IsNullOrWhiteSpace(path))
        {
            return ParseResult.Success(new ReplayRequest
            {
                Kind = "local",
                Value = path,
                SuggestedName = NullIfBlank(query["name"]),
            }, actionName, gameId, thenWatch);
        }

        // A capture needs a replay it can fetch or find; the archive-id form has no provider.
        if (isDump || isDeep)
            return ParseResult.Fail($"A {actionName} link needs the replay's 'url' or 'path'.");

        var id2 = query["id"];
        if (string.IsNullOrWhiteSpace(id2))
        {
            // paladin://replay/<match-id>
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) id2 = segments[^1];
        }

        if (!string.IsNullOrWhiteSpace(id2))
        {
            var source = NullIfBlank(query["source"]) ?? "aoe4replays";
            return ParseResult.Success(new ReplayRequest
            {
                Kind = source,
                Value = id2,
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

    /// <summary>paladin://dump?game=&lt;id&gt;&amp;url=...&amp;url=... — what the site's "Dump this game" button emits (§717 §4.1); thenWatch adds the page's "Dump, then watch".</summary>
    public static string BuildDumpLink(long gameId, IEnumerable<string> replayUrls, bool thenWatch = false) =>
        $"{Scheme}://{DumpAction}?game={gameId.ToString(CultureInfo.InvariantCulture)}&"
        + string.Join("&", replayUrls.Select(u => $"url={Uri.EscapeDataString(u)}"))
        + (thenWatch ? $"&then={ThenWatchValue}" : "");

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
