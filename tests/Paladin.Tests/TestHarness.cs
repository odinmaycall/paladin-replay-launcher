namespace Paladin.Tests;

/// <summary>
/// A deliberately tiny test runner. The alternative was xunit, which would make the
/// build depend on a NuGet restore; this project must stay buildable and verifiable
/// on a machine with no network, so the harness is 60 lines instead of a package.
/// </summary>
public static class TestHarness
{
    private static readonly List<(string Name, Action Body)> Tests = new();
    private static string _suite = "";

    public static void Suite(string name) => _suite = name;

    public static void Test(string name, Action body) => Tests.Add(($"{_suite} :: {name}", body));

    public static int Run()
    {
        var failures = new List<(string Name, Exception Error)>();
        var passed = 0;

        foreach (var (name, body) in Tests)
        {
            try
            {
                body();
                passed++;
                Console.WriteLine($"  PASS  {name}");
            }
            catch (Exception ex)
            {
                failures.Add((name, ex));
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAIL  {name}");
                Console.ResetColor();
                Console.WriteLine($"        {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {passed} passed, {failures.Count} failed, {Tests.Count} total");
        if (failures.Count > 0)
        {
            Console.WriteLine();
            foreach (var (name, error) in failures)
                Console.WriteLine($"  {name}\n{error}\n");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    // ---- assertions ---------------------------------------------------------------

    public static void True(bool condition, string because)
    {
        if (!condition) throw new AssertionException($"Expected true: {because}");
    }

    public static void False(bool condition, string because)
    {
        if (condition) throw new AssertionException($"Expected false: {because}");
    }

    public static void Equal<T>(T expected, T actual, string because = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"Expected <{expected}> but got <{actual}>. {because}".Trim());
    }

    public static void NotNull(object? value, string because = "")
    {
        if (value is null) throw new AssertionException($"Expected non-null. {because}".Trim());
    }

    public static void Throws<TException>(Action body, string because = "") where TException : Exception
    {
        try { body(); }
        catch (TException) { return; }
        catch (Exception ex)
        {
            throw new AssertionException($"Expected {typeof(TException).Name} but got {ex.GetType().Name}: {ex.Message}. {because}".Trim());
        }
        throw new AssertionException($"Expected {typeof(TException).Name} but nothing was thrown. {because}".Trim());
    }

    /// <summary>A scratch directory that deletes itself, for the file-touching tests.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir(string label)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "paladin-tests", $"{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string File(string relative, string content)
        {
            var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, content);
            return full;
        }

        public string Full(string relative) =>
            System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string Read(string relative) => System.IO.File.ReadAllText(Full(relative));
        public bool Exists(string relative) => System.IO.File.Exists(Full(relative));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}
