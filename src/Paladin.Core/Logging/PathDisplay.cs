namespace Paladin.Core.Logging;

/// <summary>
/// What a path looks like on the console.
///
/// The console is what people screenshot and stream, so it never carries the
/// Windows username or the folders between the profile and the game. The full
/// path still goes to the log file, where it is needed. On the development
/// machine Documents is redirected by OneDrive into a folder with an unrelated,
/// personal name, and a trailer take showed that name for four seconds; the
/// same would happen to anyone whose Documents lives somewhere they would
/// rather not broadcast.
///
/// Under the user profile: <c>~\...\My Games\Age of Empires IV</c>. Elsewhere:
/// the drive, then the last two folders. Short paths are shown whole.
/// </summary>
public static class PathDisplay
{
    public static string ForConsole(string path) =>
        ForConsole(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string ForConsole(string path, string? userProfile)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var norm = path.Replace('/', '\\').TrimEnd('\\');
        var profile = string.IsNullOrEmpty(userProfile) ? null : userProfile.Replace('/', '\\').TrimEnd('\\');

        string head;
        string rest;
        if (profile is not null && norm.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            return "~";
        }
        if (profile is not null && norm.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
        {
            head = "~";
            rest = norm[(profile.Length + 1)..];
        }
        else
        {
            var firstSep = norm.IndexOf('\\');
            if (firstSep < 0) return norm;
            head = norm[..firstSep];
            rest = norm[(firstSep + 1)..];
        }

        var segments = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 2) return head + "\\" + string.Join("\\", segments);
        return head + "\\...\\" + segments[^2] + "\\" + segments[^1];
    }
}
