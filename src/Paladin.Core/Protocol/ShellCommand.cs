namespace Paladin.Core.Protocol;

/// <summary>
/// Parsing of the shell command strings Windows stores for a URL protocol handler,
/// e.g. <c>"C:\Program Files\App\app.exe" "%1"</c>.
///
/// Lives in Paladin.Core so the parsing is unit tested: getting this wrong means the
/// self-heal either never fires (links stay broken) or fires constantly (stealing an
/// association it should have left alone).
/// </summary>
public static class ShellCommand
{
    /// <summary>The executable path from a handler command, or null if it cannot be read.</summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end <= 1) return null;
            var quoted = command[1..end];
            return quoted.Length == 0 ? null : quoted;
        }

        // Unquoted: the path runs to the first space. This is ambiguous for paths that
        // contain spaces, which is exactly why handlers should always be written quoted.
        var space = command.IndexOf(' ');
        var bare = space > 0 ? command[..space] : command;
        return bare.Length == 0 ? null : bare;
    }
}
