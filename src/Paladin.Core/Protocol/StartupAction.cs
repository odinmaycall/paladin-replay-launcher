namespace Paladin.Core.Protocol;

/// <summary>§887 — what running the program with NO arguments should do.</summary>
public enum StartupIntent
{
    /// <summary>Put this copy in place and register the link handler. The newcomer's first double-click.</summary>
    Install,

    /// <summary>Already the installed copy: say what it is and what it can do. Never reinstall itself.</summary>
    Status,
}

/// <summary>
/// §887 — DOUBLE-CLICKING THE DOWNLOAD MUST INSTALL IT.
///
/// THE PROBLEM THIS EXISTS FOR, measured on a real afternoon. The commonest thing anyone does with a
/// downloaded `.exe` is double-click it. Doing that printed a help screen and exited, so the owner
/// downloaded the launcher three times, opened it each time, and never upgraded from 0.5.0 — every
/// `paladin://` click kept running the old build, and nothing on screen said so. The install was
/// `--install`, named once in the middle of the examples.
///
/// So a bare run is now read as INTENT rather than as a request for documentation: someone who just
/// downloaded this wants it working, and `--help` is where help belongs.
///
/// THE ONE THING IT MUST NOT DO IS INSTALL ITSELF ONTO ITSELF. The installed copy is also launched
/// with no arguments — by a `paladin://` link that fails to parse, by someone finding it in their
/// programs folder — and copying a running file over itself either fails or, worse, half-succeeds.
/// So the decision turns on WHERE THIS COPY IS RUNNING FROM, which is knowable before anything is
/// touched.
///
/// Pure and path-only, with no file system access at all, so every branch is tested without
/// installing anything on the machine running the tests.
/// </summary>
public static class StartupAction
{
    /// <summary>
    /// What a no-argument run means, given where this executable is and where the installed copy lives.
    ///
    /// Both paths are compared case-insensitively after normalisation, because Windows hands the same
    /// file back under different spellings — `C:\Users\Me\…` and `C:\USERS\ME\…` are one file, and a
    /// trailing separator or a relative segment must not make a copy look like a stranger to itself.
    /// An unknown current path (a single-file publish can report nothing) is treated as NOT installed:
    /// installing again is recoverable and harmless, while wrongly believing it is already installed
    /// leaves the newcomer exactly where they started.
    /// </summary>
    public static StartupIntent ForBareRun(string? currentExePath, string? installedExePath)
    {
        if (string.IsNullOrWhiteSpace(currentExePath) || string.IsNullOrWhiteSpace(installedExePath))
            return StartupIntent.Install;

        return SamePath(currentExePath, installedExePath) ? StartupIntent.Status : StartupIntent.Install;
    }

    /// <summary>Whether two paths name the same file, as Windows means it.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            var left = Path.GetFullPath(a.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var right = Path.GetFullPath(b.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // An unusable path is not this copy's own path.
            return false;
        }
    }

    /// <summary>
    /// §887 — what a newcomer reads when the double-click has done its job.
    ///
    /// Short, and it names the next move rather than the mechanism. Someone who has just installed a
    /// thing wants to know they can stop: the registry key and the install folder are already printed
    /// above this by the installer itself, for anyone who cares.
    /// </summary>
    public const string InstalledMessage =
        "Paladin Replay Launcher installed successfully. You can now close this window and return to Paladin.";
}
