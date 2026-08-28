namespace Paladin.Core.Shield;

/// <summary>
/// Minimal, dependency-free glob for '/'-separated relative paths.
///   *  matches any run of characters inside one segment
///   ?  matches one character inside one segment
///   ** matches zero or more whole segments
/// Matching is case-insensitive because the target is Windows.
/// </summary>
public static class PathGlob
{
    public static string Normalise(string relativePath) =>
        relativePath.Replace('\\', '/').TrimStart('/');

    public static bool IsMatch(string relativePath, string pattern)
    {
        var path = Normalise(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pat = Normalise(pattern).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return MatchSegments(path, 0, pat, 0);
    }

    public static bool IsMatchAny(string relativePath, IEnumerable<string> patterns)
    {
        foreach (var p in patterns)
            if (IsMatch(relativePath, p)) return true;
        return false;
    }

    private static bool MatchSegments(string[] path, int pi, string[] pat, int qi)
    {
        while (qi < pat.Length)
        {
            if (pat[qi] == "**")
            {
                // A trailing '**' means "everything BELOW this", so it needs at least one
                // more segment: "keyBindingProfiles/**" covers the files in that folder,
                // not the folder itself.
                if (qi == pat.Length - 1) return pi < path.Length;

                // Elsewhere it may consume zero or more segments, which is what makes the
                // leading "**/*.log" idiom match a log at the root as well as a nested one.
                for (var skip = pi; skip <= path.Length; skip++)
                    if (MatchSegments(path, skip, pat, qi + 1)) return true;
                return false;
            }

            if (pi >= path.Length) return false;
            if (!SegmentMatches(path[pi], pat[qi])) return false;
            pi++; qi++;
        }
        return pi == path.Length;
    }

    private static bool SegmentMatches(string segment, string pattern)
    {
        // Iterative wildcard match with backtracking on '*'.
        int s = 0, p = 0, starP = -1, starS = 0;
        while (s < segment.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], segment[s])))
            {
                s++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++; starS = s;
            }
            else if (starP >= 0)
            {
                p = starP + 1; s = ++starS;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
