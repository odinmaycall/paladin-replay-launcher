namespace Paladin.Core.Shield;

/// <summary>
/// Decides which files under the AoE4 Documents root are "protected content".
/// Every read and write the Shield performs is gated through here, which is how
/// the "never touch anything outside the protected paths" rule is enforced in one place.
/// </summary>
public sealed class ProtectionPolicy
{
    private readonly IReadOnlyList<string> _include;
    private readonly IReadOnlyList<string> _exclude;

    public ProtectionPolicy(IEnumerable<string> includePatterns, IEnumerable<string> excludePatterns)
    {
        _include = includePatterns.Select(PathGlob.Normalise).ToList();
        _exclude = excludePatterns.Select(PathGlob.Normalise).ToList();
    }

    public IReadOnlyList<string> IncludePatterns => _include;
    public IReadOnlyList<string> ExcludePatterns => _exclude;

    public bool IsProtected(string relativePath)
    {
        var rel = PathGlob.Normalise(relativePath);
        if (rel.Length == 0) return false;
        if (PathGlob.IsMatchAny(rel, _exclude)) return false;
        return PathGlob.IsMatchAny(rel, _include);
    }

    /// <summary>
    /// True when a directory could still contain protected files, so enumeration
    /// can prune obviously-irrelevant trees (playback/, LogFiles/) instead of walking them.
    /// </summary>
    public bool CouldContainProtected(string relativeDirectory)
    {
        var rel = PathGlob.Normalise(relativeDirectory);
        if (rel.Length == 0) return true;

        // If the directory itself is excluded outright, prune it.
        foreach (var ex in _exclude)
        {
            var exSegments = ex.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (exSegments.Length >= 2 && exSegments[^1] == "**")
            {
                var prefix = string.Join('/', exSegments[..^1]);
                if (PathGlob.IsMatch(rel, prefix)) return false;
            }
        }

        foreach (var inc in _include)
            if (PrefixCouldMatch(rel, inc)) return true;

        return false;
    }

    private static bool PrefixCouldMatch(string relativeDirectory, string includePattern)
    {
        var dir = relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pat = includePattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < dir.Length; i++)
        {
            if (i >= pat.Length) return false;
            if (pat[i] == "**") return true;
            if (!PathGlob.IsMatch(dir[i], pat[i])) return false;
        }
        // Every directory segment matched a pattern prefix, and the pattern has more
        // to say (a file name or deeper segments), so protected files may live below.
        return pat.Length > dir.Length;
    }
}
