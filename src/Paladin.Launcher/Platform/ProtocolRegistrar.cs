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

            using var command = Registry.CurrentUser.CreateSubKey($@"{SchemeKey}\shell\open\command");
            command.SetValue(null, $"\"{executablePath}\" \"%1\"");

            log.Info($"Registered {PaladinUri.Scheme}:// -> \"{executablePath}\" \"%1\" (per-user, HKCU)");
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
            log.Info($"Unregistered {PaladinUri.Scheme}://");
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            log.Error($"Could not unregister the {PaladinUri.Scheme}:// scheme", ex);
            return false;
        }
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
