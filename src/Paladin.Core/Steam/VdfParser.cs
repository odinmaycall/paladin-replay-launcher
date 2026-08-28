namespace Paladin.Core.Steam;

/// <summary>
/// Just enough of Valve's KeyValues text format to read libraryfolders.vdf and
/// appmanifest_*.acf. Kept in Paladin.Core, with no file access, so the parsing rules
/// are unit tested against real fixture text rather than only on a machine with Steam.
/// </summary>
public static class VdfParser
{
    public sealed class VdfNode
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public VdfNode? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;
    }

    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        var stack = new Stack<VdfNode>();
        stack.Push(root);

        string? pendingKey = null;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c == '{')
            {
                var node = new VdfNode();
                if (pendingKey is not null)
                {
                    stack.Peek().Children[pendingKey] = node;
                    pendingKey = null;
                }
                stack.Push(node);
                i++;
                continue;
            }

            if (c == '}')
            {
                if (stack.Count > 1) stack.Pop();
                i++;
                continue;
            }

            if (c == '"')
            {
                var token = ReadQuoted(text, ref i);
                if (pendingKey is null) pendingKey = token;
                else
                {
                    stack.Peek().Values[pendingKey] = token;
                    pendingKey = null;
                }
                continue;
            }

            // Unquoted token (rare but legal).
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"' && text[i] != '{' && text[i] != '}') i++;
            var bare = text[start..i];
            if (bare.Length == 0) { i++; continue; }
            if (pendingKey is null) pendingKey = bare;
            else { stack.Peek().Values[pendingKey] = bare; pendingKey = null; }
        }

        return root;
    }

    private static string ReadQuoted(string text, ref int i)
    {
        i++; // opening quote
        var sb = new System.Text.StringBuilder();
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
                sb.Append(text[i] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var other => other });
            }
            else sb.Append(text[i]);
            i++;
        }
        i++; // closing quote
        return sb.ToString();
    }

    /// <summary>
    /// Every Steam library path from libraryfolders.vdf that lists <paramref name="appId"/>,
    /// most specific first. Libraries with no "apps" block are returned as fallbacks,
    /// because older Steam clients did not write one.
    /// </summary>
    public static List<string> LibrariesForApp(string libraryFoldersVdf, int appId)
    {
        var root = Parse(libraryFoldersVdf);
        var libs = root.Child("libraryfolders") ?? root;

        var owning = new List<string>();
        var fallback = new List<string>();

        foreach (var (_, entry) in libs.Children)
        {
            var path = entry.Value("path");
            if (string.IsNullOrWhiteSpace(path)) continue;

            var apps = entry.Child("apps");
            if (apps is null) fallback.Add(path);
            else if (apps.Values.ContainsKey(appId.ToString())) owning.Add(path);
        }

        owning.AddRange(fallback);
        return owning;
    }

    /// <summary>The "installdir" from an appmanifest_&lt;id&gt;.acf, relative to steamapps/common.</summary>
    public static string? InstallDirFromAppManifest(string acfText) =>
        (Parse(acfText).Child("AppState") ?? Parse(acfText)).Value("installdir");
}
