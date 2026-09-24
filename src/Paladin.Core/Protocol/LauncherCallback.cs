namespace Paladin.Core.Protocol;

/// <summary>
/// §869 — TELLING THE BROWSER WHAT THIS LAUNCHER CAN DO, in one shell-open and nothing else.
///
/// THE CONSTRAINT THIS WORKS AROUND. A web page cannot detect a locally registered protocol handler:
/// clicking `paladin://…` either does something or does nothing, and the page is told neither way. That
/// is a browser security property, not a gap. So the site cannot know whether Deep Capture will work
/// until something on this side says so.
///
/// AND THE SITE DOES NOT WAIT FOR IT. The chip is live whether or not this callback has ever fired,
/// because a launcher that is present and capable is the common case and gating the button on a
/// handshake that has not happened yet would block the very first use on every machine. What this adds
/// is the ability to be HONEST AFTERWARDS: once a launcher has announced itself, the site can stop
/// offering Deep Capture on a launcher that genuinely cannot do it, and can name the version needed.
///
/// IT RIDES ON ANY ACTION. The user just clicked a link in their browser, so the browser is to hand and
/// opening one URL in it costs nothing and surprises nobody. A watch, a dump or a capture all announce.
///
/// NO POLLING, NO ENDPOINT, NO SERVER STATE, NO IDENTITY. Two query parameters, read once and cleaned
/// out of the address bar by the page.
/// </summary>
public static class LauncherCallback
{
    /// <summary>The site's own parameter names (src/lib/launcherCapability.ts).</summary>
    public const string VersionParam = "launcher";
    public const string CapsParam = "caps";

    /// <summary>The capability the Deep Capture chip asks for by name.</summary>
    public const string DeepCapture = "deepCapture";

    /// <summary>What this build can do, by name. Named rather than versioned so the site never encodes version numbers.</summary>
    public static IReadOnlyList<string> Capabilities { get; } = new[] { DeepCapture };

    /// <summary>
    /// The URL to open, or null when there is nothing sensible to announce.
    ///
    /// `site` is the configured upload address, so a run pointed at a test deploy announces to that
    /// deploy rather than to production — the same rule the Deep uploader follows.
    /// </summary>
    public static string? UrlFor(string? site, string? version, IReadOnlyList<string>? capabilities = null)
    {
        var clean = (version ?? "").Trim();
        // The page refuses anything that is not a plain version, so do not send one.
        if (clean.Length == 0 || clean.Length > 32 || !clean.All(c => char.IsLetterOrDigit(c) || c is '.' or '+' or '-' or '_')) return null;

        var caps = (capabilities ?? Capabilities)
            .Select(c => (c ?? "").Trim())
            .Where(c => c.Length > 0 && c.Length <= 40 && c.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            .ToList();
        if (caps.Count == 0) return null;

        var origin = OriginOf(site);
        return $"{origin}?{VersionParam}={Uri.EscapeDataString(clean)}&{CapsParam}={Uri.EscapeDataString(string.Join(",", caps))}";
    }

    /// <summary>The scheme and authority of the configured address, with a trailing slash. Never a path.</summary>
    public static string OriginOf(string? site)
    {
        var text = string.IsNullOrWhiteSpace(site) ? "https://paladin.odinmaycall.com" : site.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return "https://paladin.odinmaycall.com/";
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return "https://paladin.odinmaycall.com/";
        return $"{parsed.Scheme}://{parsed.Authority}/";
    }
}
