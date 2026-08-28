using System.Text.Json;
using System.Text.Json.Serialization;

namespace Paladin.Core.Config;

/// <summary>
/// Everything a tester may need to change without a rebuild. Written to
/// %LOCALAPPDATA%\PaladinReplayLauncher\config.json on first run.
/// </summary>
public sealed class LauncherConfig
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // ---- Paladin Shield: what gets protected -----------------------------------------
    //
    // Patterns are relative to the AoE4 Documents root
    // (Documents\My Games\Age of Empires IV) and always use '/'.
    //   *   matches within one path segment
    //   **  matches any number of segments
    //
    // The default set is deliberately CONSERVATIVE: settings, keybinds and per-profile
    // user configuration only. Progression data (datastore/*.rlt, Savegames) is NOT
    // protected by default because a user may legitimately change it in the same AoE4
    // run as the replay, and rolling that back would destroy real progress.

    public List<string> ProtectedPatterns { get; set; } = new()
    {
        "configuration_system.lua",
        "local.ini",
        "keyBindingProfiles/**",
        "Users/*/configuration_user.lua",
        "Users/*/cloud/configuration_user.lua",
    };

    /// <summary>Opt-in extras. Merged into ProtectedPatterns only when ProtectExtended is true.</summary>
    public List<string> ExtendedPatterns { get; set; } = new()
    {
        "Users/*/datastore/**",
        "Users/*/savedShoppingCart.sav",
    };

    public bool ProtectExtended { get; set; }

    /// <summary>
    /// Never snapshot or restore anything matching these, even if a protected pattern
    /// would otherwise catch it. Logs, caches and replays are churn, not settings.
    /// </summary>
    public List<string> ExcludedPatterns { get; set; } = new()
    {
        "**/*.log",
        "LogFiles/**",
        "Cache/**",
        "playback/**",
        "matchhistory/**",
        "network/**",
        "Scratch/**",
        "screenshots/**",
        "warnings*.log",
        "appisrunning.bin",
    };

    /// <summary>Refuse to snapshot a single protected file larger than this (guards against a runaway path).</summary>
    public long MaxProtectedFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Config keys Age of Empires IV rewrites on every launch. Measured on a live install:
    /// a completely ordinary game start (no replay, no -dev) changes four protected files,
    /// and these are the only lines that differ. Ignoring them is what stops the Shield
    /// reporting a false alarm — and performing a pointless restore — every single launch.
    /// Add to this list if further bookkeeping keys turn up.
    /// </summary>
    public List<string> VolatileKeys { get; set; } = new()
    {
        "last_modified",
        "app_runs_count",
        "most_recently_viewed_profile_id",
    };

    // ---- Launch ----------------------------------------------------------------------

    public int SteamAppId { get; set; } = 1466860;

    /// <summary>
    /// Phase 4 knob. aoe4replays.gg uses -dev; we want to test whether it is required.
    /// </summary>
    public bool UseDevFlag { get; set; } = true;

    /// <summary>
    /// Argument template. {appid} {devflag} {replayarg} are substituted.
    /// Kept as a template so a tester can try other forms without a rebuild.
    /// </summary>
    public string LaunchArgumentTemplate { get; set; } = "-applaunch {appid} {devflag} -replay {replayarg}";

    /// <summary>
    /// How the replay is named to AoE4. {name} is the file name placed in the playback folder.
    /// </summary>
    public string ReplayArgumentTemplate { get; set; } = "playback:{name}";

    /// <summary>
    /// Subfolder of the AoE4 Documents root the replay is written into, '/'-separated.
    /// Observed: aoe4replays.gg-style downloads land directly in "playback".
    /// </summary>
    public string PlaybackSubfolder { get; set; } = "playback";

    /// <summary>
    /// Observed on a real install: downloaded replays in playback/ have NO extension
    /// (e.g. AgeIV_Replay_207502139) while game-recorded ones in playback/replays/ are .rec.
    /// Stripping matches the working aoe4replays.gg convention.
    /// </summary>
    public bool StripReplayExtension { get; set; } = true;

    /// <summary>Process names (no extension) that mean "AoE4 itself is running".</summary>
    public List<string> GameProcessNames { get; set; } = new() { "RelicCardinal", "AoE4", "AoEIV" };

    /// <summary>How long to wait for the game process to appear after asking Steam to launch it.</summary>
    public int GameStartTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// The same wait for --observe, where the person has to go and start the game
    /// themselves. Much longer, because the clock covers reading the prompt, finding
    /// Steam and the game's own start-up, not just the launch.
    /// </summary>
    public int ObserveStartTimeoutSeconds { get; set; } = 1800;

    /// <summary>Extra settle time after the game process exits, before snapshotting again (flush of config writes).</summary>
    public int PostExitSettleSeconds { get; set; } = 5;

    // ---- Restore behaviour -------------------------------------------------------------

    /// <summary>
    /// Files that did not exist before the replay but do afterwards, inside protected paths.
    /// When true they are MOVED to the session quarantine folder, never hard-deleted.
    /// </summary>
    public bool QuarantineCreatedFiles { get; set; } = true;

    /// <summary>
    /// Before restoring, copy each changed or created file into &lt;session&gt;/changed/.
    /// On by default because the central open question — whether replay launching really
    /// damages settings or merely rewrites routine bookkeeping — can only be answered by
    /// diffing the modified file against the backup, and restoring destroys the evidence.
    /// </summary>
    public bool SaveChangedCopies { get; set; } = true;

    /// <summary>Delete the replay the launcher placed in playback/ once the session ends.</summary>
    public bool CleanUpPreparedReplay { get; set; } = true;

    /// <summary>Delete a session folder after a fully verified restore. Failed restores are always kept.</summary>
    public bool CleanUpSessionOnSuccess { get; set; } = true;

    /// <summary>Sessions kept on disk regardless, newest first, for post-mortem.</summary>
    public int KeepRecentSessions { get; set; } = 10;

    // ---- Overrides for when auto-detection fails -----------------------------------------

    public string? SteamExeOverride { get; set; }
    public string? Aoe4InstallDirOverride { get; set; }
    public string? Aoe4DocumentsPathOverride { get; set; }

    // -------------------------------------------------------------------------------------

    public IReadOnlyList<string> EffectiveProtectedPatterns()
    {
        var all = new List<string>(ProtectedPatterns);
        if (ProtectExtended) all.AddRange(ExtendedPatterns);
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static LauncherConfig FromJson(string json) =>
        JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions)
        ?? throw new InvalidDataException("Config JSON deserialised to null.");

    public static LauncherConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try { return FromJson(File.ReadAllText(path)); }
            catch (JsonException)
            {
                // A corrupt config must never block a restore; fall back to defaults and
                // keep the bad file beside it for inspection.
                var bad = path + ".invalid";
                try { File.Copy(path, bad, overwrite: true); } catch (IOException) { }
            }
        }
        var fresh = new LauncherConfig();
        Save(fresh, path);
        return fresh;
    }

    public static void Save(LauncherConfig config, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, config.ToJson());
    }
}
