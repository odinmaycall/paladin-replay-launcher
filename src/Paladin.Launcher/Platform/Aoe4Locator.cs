using System.Runtime.Versioning;
using Microsoft.Win32;
using Paladin.Core.Config;
using Paladin.Core.Logging;
using Paladin.Core.Replay;
using Paladin.Core.Steam;

namespace Paladin.Launcher.Platform;

public sealed class Aoe4Environment
{
    public string? SteamExePath { get; init; }
    public string? Aoe4InstallDir { get; init; }
    public string? Aoe4GameExePath { get; init; }
    public string? Aoe4DocumentsPath { get; init; }
    public string? PlaybackPath { get; init; }

    /// <summary>Full file version of the game binary, e.g. "16.3.11308.0".</summary>
    public string? Aoe4GameVersion { get; init; }

    /// <summary>
    /// The build component of that version (11308 in the example), which is the same
    /// number a replay stores in its header. Null when it could not be read.
    /// </summary>
    public int? Aoe4GameBuild { get; init; }

    public List<string> Problems { get; } = new();

    /// <summary>The Documents folder is the only hard requirement for the Shield.</summary>
    public bool CanProtect => !string.IsNullOrEmpty(Aoe4DocumentsPath) && Directory.Exists(Aoe4DocumentsPath);

    /// <summary>Steam is how we launch; without it we cannot start a replay.</summary>
    public bool CanLaunch => !string.IsNullOrEmpty(SteamExePath) && File.Exists(SteamExePath);
}

/// <summary>
/// Finds Steam, the AoE4 install, and the AoE4 Documents folder without ever
/// hard-coding a user name or drive. Every lookup goes through a Windows API or the
/// registry; failure produces a described problem, not an exception.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Aoe4Locator
{
    /// <summary>Verified on a live install: the AoE4 game binary is RelicCardinal.exe.</summary>
    public static readonly string[] KnownGameExeNames = { "RelicCardinal.exe", "AoE4.exe", "RelicCardinal64.exe" };

    public static Aoe4Environment Detect(LauncherConfig config, PaladinLog log)
    {
        var steamExe = config.SteamExeOverride ?? FindSteamExe(log);
        var installDir = config.Aoe4InstallDirOverride ?? FindAoe4InstallDir(steamExe, config.SteamAppId, log);
        var gameExe = installDir is null ? null : FindGameExe(installDir);
        var documents = config.Aoe4DocumentsPathOverride ?? FindAoe4DocumentsPath(log);

        var gameVersion = gameExe is null ? null : ReadFileVersion(gameExe, log);

        var env = new Aoe4Environment
        {
            SteamExePath = steamExe,
            Aoe4InstallDir = installDir,
            Aoe4GameExePath = gameExe,
            Aoe4GameVersion = gameVersion,
            Aoe4GameBuild = BuildCompatibility.ParseGameBuild(gameVersion),
            Aoe4DocumentsPath = documents,
            PlaybackPath = documents is null
                ? null
                : Path.Combine(documents, config.PlaybackSubfolder.Replace('/', Path.DirectorySeparatorChar)),
        };

        if (steamExe is null)
            env.Problems.Add("Steam could not be found. Set \"SteamExeOverride\" in config.json to the full path of steam.exe.");
        else if (!File.Exists(steamExe))
            env.Problems.Add($"Steam was located at '{steamExe}' but that file does not exist.");

        if (installDir is null)
            env.Problems.Add($"The Age of Empires IV install (Steam app {config.SteamAppId}) could not be found. Set \"Aoe4InstallDirOverride\" in config.json.");
        else if (gameExe is null)
            env.Problems.Add($"Found the AoE4 folder at '{installDir}' but none of {string.Join(", ", KnownGameExeNames)} inside it. Process monitoring will fall back to process names.");

        if (documents is null)
            env.Problems.Add("The Age of Empires IV Documents folder could not be found. Set \"Aoe4DocumentsPathOverride\" in config.json. Paladin Shield cannot protect settings without it.");

        log.Info($"Steam:      {steamExe ?? "(not found)"}");
        log.Info($"AoE4 game:  {gameExe ?? installDir ?? "(not found)"}");
        log.Info($"AoE4 build: {gameVersion ?? "(unknown)"}");
        log.Info($"AoE4 docs:  {documents ?? "(not found)"}");
        return env;
    }

    /// <summary>
    /// The game binary's file version, e.g. "16.3.11308.0". Confirmed on a live install
    /// that the third component matches the build a replay records in its header.
    /// Returns null on any failure so the build check simply says nothing rather than
    /// producing a warning from a value it could not read.
    /// </summary>
    public static string? ReadFileVersion(string exePath, PaladinLog log)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var version = info.FileVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Debug($"Could not read the version of {exePath}: {ex.Message}");
            return null;
        }
    }

    // ---- Steam -------------------------------------------------------------------

    public static string? FindSteamExe(PaladinLog log)
    {
        // HKCU carries the running user's own install and is the most reliable.
        var fromHkcu = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe");
        if (fromHkcu is not null)
        {
            var normalised = Path.GetFullPath(fromHkcu.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(normalised)) return normalised;
            log.Debug($"HKCU SteamExe pointed at a missing file: {normalised}");
        }

        foreach (var (hive, key) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            var installPath = ReadRegistryString(hive, key, "SteamPath") ?? ReadRegistryString(hive, key, "InstallPath");
            if (installPath is null) continue;
            var candidate = Path.Combine(Path.GetFullPath(installPath.Replace('/', Path.DirectorySeparatorChar)), "steam.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var guess in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe"),
                 })
        {
            if (File.Exists(guess)) return guess;
        }

        return null;
    }

    /// <summary>Every Steam library root on this machine, from libraryfolders.vdf.</summary>
    public static List<string> SteamLibrariesForApp(string steamRoot, int appId, PaladinLog log)
    {
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
        {
            log.Debug($"No libraryfolders.vdf at {vdf}; assuming the default library only.");
            return new List<string> { steamRoot };
        }

        try
        {
            var libs = VdfParser.LibrariesForApp(File.ReadAllText(vdf), appId);
            if (!libs.Contains(steamRoot, StringComparer.OrdinalIgnoreCase)) libs.Add(steamRoot);
            return libs;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            log.Warn($"Could not read {vdf}: {ex.Message}");
            return new List<string> { steamRoot };
        }
    }

    public static string? FindAoe4InstallDir(string? steamExe, int appId, PaladinLog log)
    {
        if (steamExe is null) return null;
        var steamRoot = Path.GetDirectoryName(steamExe);
        if (steamRoot is null) return null;

        foreach (var library in SteamLibrariesForApp(steamRoot, appId, log))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;

            string? installDirName;
            try { installDirName = VdfParser.InstallDirFromAppManifest(File.ReadAllText(manifest)); }
            catch (IOException ex) { log.Warn($"Could not read {manifest}: {ex.Message}"); continue; }

            if (string.IsNullOrWhiteSpace(installDirName)) continue;

            var full = Path.Combine(library, "steamapps", "common", installDirName);
            if (Directory.Exists(full)) return full;
            log.Warn($"appmanifest named '{installDirName}' but {full} does not exist.");
        }

        // Last resort: the well-known folder name in any library.
        foreach (var library in SteamLibrariesForApp(steamRoot, appId, log))
        {
            var guess = Path.Combine(library, "steamapps", "common", "Age of Empires IV");
            if (Directory.Exists(guess)) return guess;
        }

        return null;
    }

    public static string? FindGameExe(string installDir)
    {
        foreach (var name in KnownGameExeNames)
        {
            var candidate = Path.Combine(installDir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- Documents ---------------------------------------------------------------

    /// <summary>
    /// Locates Documents\My Games\Age of Empires IV.
    ///
    /// This must not assume %USERPROFILE%\Documents: on the development machine
    /// Documents is redirected by OneDrive to a folder with an unrelated name.
    /// SpecialFolder.MyDocuments reads the shell's own Personal path and follows that
    /// redirect; the extra candidates are fallbacks for unusual setups.
    /// </summary>
    public static string? FindAoe4DocumentsPath(PaladinLog log)
    {
        const string relative = @"My Games\Age of Empires IV";

        foreach (var root in DocumentsCandidates())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.Combine(root, relative);
            if (Directory.Exists(candidate))
            {
                log.Debug($"AoE4 Documents resolved via '{root}'");
                return candidate;
            }
        }
        return null;
    }

    public static IEnumerable<string> DocumentsCandidates()
    {
        // 1. The shell's Personal known folder — honours OneDrive/redirection.
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        // 2. The registry value the shell folder is derived from, unexpanded.
        var shellPersonal = ReadRegistryString(
            Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
            "Personal");
        if (shellPersonal is not null)
            yield return Environment.ExpandEnvironmentVariables(shellPersonal);

        // 3. OneDrive roots, in case the game folder lives there but Personal points elsewhere.
        foreach (var variable in new[] { "OneDriveCommercial", "OneDriveConsumer", "OneDrive" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) yield return Path.Combine(value, "Documents");
        }

        // 4. The plain profile path, last.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) yield return Path.Combine(profile, "Documents");
    }

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
