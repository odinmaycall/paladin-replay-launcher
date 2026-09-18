namespace Paladin.Core.Dump;

/// <summary>What a cancelled run does about the game it may or may not have started.</summary>
public enum InterruptedLaunchAction
{
    /// <summary>A watch, or nothing was ever launched: the game is the user's, or there is none.</summary>
    LeaveItAlone,

    /// <summary>The game is already running and this session started it: end it before the restore.</summary>
    EndTheGame,

    /// <summary>
    /// Steam was asked and the game has not appeared yet: wait a little for it, then end it.
    /// </summary>
    WaitForItThenEnd,
}

/// <summary>
/// The decision a Ctrl+C makes about a game that was launched but may not be visible yet.
///
/// It exists as a pure record because of a live failure. In session 20260918-161828-81e679
/// the owner pressed Ctrl+C 374 ms after the Steam launch command went out
/// ("Steam launch requested (pid 61324)" at 16:18:29.044, "Session cancelled by the user"
/// at 16:18:29.418). The cancel path looked for a game process, found none — the game
/// takes about five seconds to appear — restored the settings and deleted the prepared
/// replay, and the game then started on its own with nothing watching it and no replay to
/// play. The window between "Steam was asked" and "the process exists" was the whole bug,
/// and the fix is to wait it out before the settings check rather than to look once.
///
/// <see cref="Config.LauncherConfig.GameStartTimeoutSeconds"/> (300 s) is the wait for a launch
/// that is meant to succeed and is far too long to hold a cancelled run open; this is its
/// own short, stated grace.
///
/// That grace is counted from the launch command, not from the cancel, because the bug
/// only exists while Steam is still producing the game. A user who has watched "Waiting
/// for Age of Empires IV to start ..." for three minutes and then pressed Ctrl+C is
/// telling this code the game is not coming: they get what they got before this was
/// written — straight to the settings check, nothing new to wait for and nothing to warn
/// about.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="AppearGrace">How long to wait for the process to appear (zero unless waiting).</param>
/// <param name="CloseGrace">How long the game then gets to close by itself before it is ended.</param>
public sealed record InterruptedLaunchPlan(InterruptedLaunchAction Action, TimeSpan AppearGrace, TimeSpan CloseGrace)
{
    /// <summary>
    /// The grace a cancelled dump gives Steam to produce the game. Measured on this
    /// machine, the gap between the launch command and the process appearing is about five
    /// seconds (§717 §2.4, "Steam to process ~5 s"); 25 s covers a cold Steam, an update
    /// check or a slow disk without leaving a cancelled run sitting there.
    /// </summary>
    public const int AppearGraceSeconds = 25;

    private static readonly InterruptedLaunchPlan Nothing =
        new(InterruptedLaunchAction.LeaveItAlone, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    /// <paramref name="cancelPolicy"/> is the session's own: a watch's is
    /// <see cref="ExitPolicy.WaitForUser"/> and nothing below applies to it — the user
    /// asked for that game and it stays. A dump's is EndProcess, because its game was
    /// started only to be read and a -dev game still holding its console writes local.ini
    /// back over the restore when it is finally closed.
    /// </summary>
    /// <param name="launchedAtUtc">
    /// When the Steam launch command went out, or null if it never did. What is left of
    /// the grace is measured from it, so a cancel long after a launch Steam never
    /// fulfilled waits for nothing.
    /// </param>
    /// <param name="gameSeen">True once the game process has been found.</param>
    /// <param name="nowUtc">The moment of the cancel.</param>
    public static InterruptedLaunchPlan For(
        ExitPolicy cancelPolicy, DateTime? launchedAtUtc, bool gameSeen, DateTime nowUtc)
    {
        if (cancelPolicy.Action != ExitAction.EndProcess) return Nothing;
        if (launchedAtUtc is null) return Nothing;

        if (gameSeen)
            return new InterruptedLaunchPlan(InterruptedLaunchAction.EndTheGame, TimeSpan.Zero, cancelPolicy.Grace);

        var left = TimeSpan.FromSeconds(AppearGraceSeconds) - (nowUtc - launchedAtUtc.Value);
        if (left <= TimeSpan.Zero) return Nothing;

        return new InterruptedLaunchPlan(InterruptedLaunchAction.WaitForItThenEnd, left, cancelPolicy.Grace);
    }
}
