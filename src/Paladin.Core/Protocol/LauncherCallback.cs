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

    /// <summary>
    /// 884 - CAN THIS BUILD COMPLETE A PARTIAL CAPTURE FROM A LINK?
    ///
    /// Two things have to be true at once, which is why it is ONE name rather than two. The build must
    /// understand `force=1` on a paladin://deep link (884), or its own pre-flight refuses a game
    /// Paladin already holds and the retry is a dead end. And it must RETAIN THE REPLAY (879), or the
    /// re-capture produces another sampler-only artifact and the package is partial all over again.
    ///
    /// A launcher that has one without the other would send a reader through a five-minute capture
    /// that cannot finish the job, so the site asks for this name before it offers the retry at all -
    /// and shows the update message instead when it is absent. Named rather than versioned, exactly as
    /// deepCapture is, so the site never encodes a version number.
    ///
    /// 890 - AND IT NO LONGER CARRIES THE SECOND HALF. See ReplayRetention below: this name now means
    /// only "understands force=1", because a build announced it while its retention was broken.
    /// </summary>
    public const string DeepRetry = "deepRetry";

    /// <summary>
    /// 890 - DOES THE REPLAY ACTUALLY SURVIVE A CAPTURE MADE BY THIS BUILD?
    ///
    /// deepRetry was written to mean two things at once - understands force=1, AND retains the replay -
    /// and for one release that was a claim no build could falsify. 0.5.3 announced deepRetry while its
    /// retention was broken by an ordering bug (888): it read the replay from a path its own run had
    /// already deleted. Every capture it made succeeded, uploaded, and left the package PARTIAL - and
    /// the site then offered a retry that would do the same thing again, for ever.
    ///
    /// The fault was not the reasoning behind deepRetry, which was right. It was that ONE NAME COVERED
    /// TWO INDEPENDENT FACTS, so a build could satisfy half of it and still say the word. Retention now
    /// has its own name, and a build that cannot keep a replay simply does not say it.
    ///
    /// THE POINT OF A NAMED CAPABILITY IS THAT IT CAN BE WITHHELD. A version number cannot express
    /// "this build captures but must not be asked to complete a package"; a missing name says exactly
    /// that, and the site turns it into "update your launcher" rather than into a link that wastes five
    /// minutes of someone's evening.
    /// </summary>
    public const string ReplayRetention = "replayRetention";

    /// <summary>
    /// What this build can do, by name. Named rather than versioned so the site never encodes version
    /// numbers, and ORDERED, because the site's contract test pins this exact string: a reordering
    /// would read as a contract change when nothing had actually changed.
    /// </summary>
    public static IReadOnlyList<string> Capabilities { get; } = new[] { DeepCapture, DeepRetry, ReplayRetention };

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
