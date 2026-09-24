using System.Runtime.Versioning;
using Microsoft.Win32;
using Paladin.Core.Logging;
using Paladin.Core.Protocol;

namespace Paladin.Launcher.Platform;

/// <summary>
/// Registers the paladin:// URI scheme.
///
/// Registration goes under HKEY_CURRENT_USER\Software\Classes, which is per-user and
/// needs no administrator rights — a launcher that demanded elevation just to open a
/// replay link would be a bad neighbour on someone's gaming PC.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ProtocolRegistrar
{
    private const string ClassesRoot = @"Software\Classes";
    private static string SchemeKey => $@"{ClassesRoot}\{PaladinUri.Scheme}";

    /// <summary>
    /// §872 — WINDOWS 11 WANTS THE APP DECLARED, not just the scheme.
    ///
    /// A scheme under Software\Classes is enough for classic ShellExecute — `Start-Process
    /// "paladin://…"` launches the app from an ordinary unelevated process, which is exactly the
    /// browser's own context. A BROWSER asking Windows to open the same link can still get
    /// "Get an app to open this 'paladin' link", because the modern default-apps path looks the app up
    /// through RegisteredApplications -> Capabilities -> URLAssociations, and an app that never
    /// declared itself there is not found by it.
    ///
    /// Measured on the owner's machine after a full restart: the class key correct, no UserChoice
    /// override, no HKLM shadow, no Mark-of-the-Web, Smart App Control off, the browser's own protocol
    /// preferences empty — and the shell launching it happily while the browser would not. The one
    /// thing missing was this.
    /// </summary>
    private const string ProgId = "PaladinReplayLauncher.Url";
    private const string CapabilitiesKey = @"Software\Paladin Replay Launcher\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private const string RegisteredApplicationsValue = "Paladin Replay Launcher";

    public static bool IsRegistered(out string? currentCommand)
    {
        currentCommand = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{SchemeKey}\shell\open\command");
            currentCommand = key?.GetValue(null) as string;
            return currentCommand is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static bool Register(string executablePath, PaladinLog log)
    {
        if (!File.Exists(executablePath))
        {
            log.Error($"Cannot register {PaladinUri.Scheme}:// — '{executablePath}' does not exist.");
            return false;
        }

        try
        {
            using var scheme = Registry.CurrentUser.CreateSubKey(SchemeKey);
            scheme.SetValue(null, $"URL:Paladin Replay Launcher");
            scheme.SetValue("URL Protocol", "");

            using (var icon = scheme.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{executablePath}\",0");

            using (var command = Registry.CurrentUser.CreateSubKey($@"{SchemeKey}\shell\open\command"))
                command.SetValue(null, $"\"{executablePath}\" \"%1\"");

            // §872 — the same handler as a ProgId, which is what URLAssociations must point at.
            using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{ProgId}"))
            {
                progId.SetValue(null, "Paladin Replay Launcher");
                using (var icon = progId.CreateSubKey("DefaultIcon"))
                    icon.SetValue(null, $"\"{executablePath}\",0");
                using var progCommand = progId.CreateSubKey(@"shell\open\command");
                progCommand.SetValue(null, $"\"{executablePath}\" \"%1\"");
            }

            // §872 — and the declaration Windows 11's default-apps path actually reads.
            using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
            {
                caps.SetValue("ApplicationName", "Paladin Replay Launcher");
                caps.SetValue("ApplicationDescription", "Opens an Age of Empires IV replay and puts your game settings back afterwards.");
                using var urls = caps.CreateSubKey("URLAssociations");
                urls.SetValue(PaladinUri.Scheme, ProgId);
            }

            using (var registered = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsKey))
                registered.SetValue(RegisteredApplicationsValue, CapabilitiesKey);

            log.Info($"Registered {PaladinUri.Scheme}:// -> \"{executablePath}\" \"%1\" (per-user, HKCU, with Capabilities)");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            log.Error($"Could not register the {PaladinUri.Scheme}:// scheme", ex);
            return false;
        }
    }

    public static bool Unregister(PaladinLog log)
    {
        try
        {
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: true);
            if (classes?.OpenSubKey(PaladinUri.Scheme) is null)
            {
                log.Info($"{PaladinUri.Scheme}:// was not registered.");
                return true;
            }
            classes.DeleteSubKeyTree(PaladinUri.Scheme);

            // §872 — take down everything Register puts up, so --uninstall leaves nothing behind and a
            // reinstall is not building on half a previous one. Each is removed independently: a
            // missing one is not a failure, it is an install that predates it.
            TryDelete(() => classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false));
            TryDelete(() =>
            {
                using var registered = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsKey, writable: true);
                registered?.DeleteValue(RegisteredApplicationsValue, throwOnMissingValue: false);
            });
            TryDelete(() =>
            {
                using var software = Registry.CurrentUser.OpenSubKey("Software", writable: true);
                software?.DeleteSubKeyTree(@"Paladin Replay Launcher", throwOnMissingSubKey: false);
            });

            log.Info($"Unregistered {PaladinUri.Scheme}://");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            log.Error($"Could not unregister the {PaladinUri.Scheme}:// scheme", ex);
            return false;
        }
    }

    /// <summary>§872 — one cleanup step. A key that is already gone is not an error worth failing over.</summary>
    private static void TryDelete(Action remove)
    {
        try { remove(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
    }

    /// <summary>
    /// Path of the running executable, for registration. Environment.ProcessPath is the
    /// only reliable answer in a single-file publish, where the managed assembly has no
    /// location on disk at all.
    /// </summary>
    public static string CurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)) return path;

        // Last resort: the app directory plus this process's name.
        var directory = AppContext.BaseDirectory;
        var name = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        return string.IsNullOrEmpty(directory) ? "" : Path.Combine(directory, name + ".exe");
    }
}
