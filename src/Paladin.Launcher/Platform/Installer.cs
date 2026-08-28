using System.Runtime.Versioning;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;

namespace Paladin.Launcher.Platform;

/// <summary>
/// Puts the launcher somewhere stable and keeps its protocol registration honest.
///
/// The problem this solves, reported from real use of a comparable launcher: the URI
/// handler stores an absolute path to the exe, and if that exe is sitting in Downloads it
/// can be moved, tidied, re-downloaded under a new name, or swept up by Storage Sense.
/// The registry entry then points at nothing, browser links silently do nothing, and the
/// user's only recourse is to "reinstall".
///
/// Two defences:
///   1. Install into %LOCALAPPDATA%\Programs, which nothing automatically cleans.
///   2. Self-heal on every run — if the registration is missing or points at a file that
///      no longer exists, re-register to wherever this exe actually is.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Installer
{
    public const string ProductFolderName = "PaladinReplayLauncher";
    public const string ExeName = "PaladinReplayLauncher.exe";

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ProductFolderName);

    public static string InstalledExePath => Path.Combine(InstallDirectory, ExeName);

    public static bool IsRunningFromInstallDirectory()
    {
        var current = ProtocolRegistrar.CurrentExecutablePath();
        if (string.IsNullOrEmpty(current)) return false;
        return string.Equals(
            Path.GetFullPath(current), Path.GetFullPath(InstalledExePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies this exe into the install directory and registers the protocol from there,
    /// so the download can then be deleted without breaking anything.
    /// </summary>
    public static bool Install(PaladinLog log, IShieldUi ui)
    {
        var source = ProtocolRegistrar.CurrentExecutablePath();
        if (string.IsNullOrEmpty(source) || !File.Exists(source))
        {
            ui.Fail("Could not work out where this program is running from, so it cannot install itself.");
            return false;
        }

        var target = InstalledExePath;
        try
        {
            Directory.CreateDirectory(InstallDirectory);

            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                // Copying over a running exe is impossible; a pending-rename dance is not
                // worth it for a prototype, so say so plainly instead of failing obscurely.
                if (File.Exists(target) && IsFileLocked(target))
                {
                    ui.Fail("An installed copy is currently running. Close it and run --install again.");
                    return false;
                }
                File.Copy(source, target, overwrite: true);
                ui.Ok($"Installed to {target}");
            }
            else
            {
                ui.Ok($"Already running from {target}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("Install copy failed", ex);
            ui.Fail($"Could not copy the program to {target}: {ex.Message}");
            return false;
        }

        if (!ProtocolRegistrar.Register(target, log))
        {
            ui.Fail("Installed, but the paladin:// link handler could not be registered.");
            return false;
        }

        ui.Ok("paladin:// links registered");
        ui.Note("You can now delete the copy you downloaded — this installed one is what runs.");
        ui.Note($"To remove it later: \"{target}\" --uninstall");
        return true;
    }

    /// <summary>
    /// Quietly repairs a broken registration. Called on every ordinary run.
    ///
    /// Deliberately conservative: it only acts when the registration is absent or points
    /// at a file that no longer exists. If it points at a different exe that DOES exist,
    /// that is someone's deliberate install and this leaves it alone rather than stealing
    /// the association out from under them.
    /// </summary>
    public static void EnsureRegistrationCurrent(PaladinLog log, IShieldUi? ui = null)
    {
        var current = ProtocolRegistrar.CurrentExecutablePath();
        if (string.IsNullOrEmpty(current) || !File.Exists(current)) return;

        var registered = ProtocolRegistrar.IsRegistered(out var command);
        var registeredExe = ShellCommand.ExtractExePath(command);

        if (registered && registeredExe is not null && File.Exists(registeredExe))
        {
            log.Debug($"paladin:// registration is intact -> {registeredExe}");
            return;
        }

        var reason = !registered
            ? "the paladin:// handler was not registered"
            : $"the registered handler pointed at a missing file ({registeredExe ?? "unparseable"})";

        log.Warn($"Self-heal: {reason}; re-registering to {current}");
        if (ProtocolRegistrar.Register(current, log))
            ui?.Note($"Repaired the paladin:// link handler ({reason}).");
    }

    public static bool Uninstall(PaladinLog log, IShieldUi ui)
    {
        var ok = ProtocolRegistrar.Unregister(log);
        ui.Ok("paladin:// link handler removed");

        if (Directory.Exists(InstallDirectory))
        {
            ui.Note($"The program itself is still at {InstallDirectory} — delete that folder to finish.");
            ui.Note("(It cannot delete itself while running.)");
        }

        var data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);
        if (Directory.Exists(data))
        {
            ui.Note($"Your settings backups and logs are kept at {data}");
            ui.Note("Those are deliberately left alone. Delete them yourself once you are sure you do not need them.");
        }
        return ok;
    }

    private static bool IsFileLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
