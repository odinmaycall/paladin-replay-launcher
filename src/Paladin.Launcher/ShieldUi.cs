namespace Paladin.Launcher;

/// <summary>
/// The progress surface. The session pipeline only ever calls this, so swapping the
/// console prototype for a WinForms window later means one new implementation and no
/// change to the Shield or the session logic.
/// </summary>
public interface IShieldUi
{
    void Header(string replayLabel);
    void Ok(string message);
    void Pending(string message);
    void Warn(string message);
    void Fail(string message);
    void Note(string message);
    void RunningBanner();
    void Done(string message);
    bool Confirm(string question, bool defaultAnswer);
}

public sealed class ConsoleShieldUi : IShieldUi
{
    private readonly bool _assumeYes;

    public ConsoleShieldUi(bool assumeYes) => _assumeYes = assumeYes;

    public void Header(string replayLabel)
    {
        Console.WriteLine();
        Console.WriteLine("  Paladin Replay Launcher");
        Console.WriteLine("  " + new string('-', 46));
        Console.WriteLine($"  Replay:  {replayLabel}");
        Console.WriteLine();
    }

    public void Ok(string message) => Write(ConsoleColor.Green, "  [ok]   ", message);
    public void Pending(string message) => Write(ConsoleColor.DarkGray, "  [..]   ", message);
    public void Warn(string message) => Write(ConsoleColor.Yellow, "  [warn] ", message);
    public void Fail(string message) => Write(ConsoleColor.Red, "  [fail] ", message);
    public void Note(string message) => Write(ConsoleColor.DarkGray, "         ", message);

    public void RunningBanner()
    {
        Console.WriteLine();
        Write(ConsoleColor.Cyan, "  ", "Replay running");
        Write(ConsoleColor.Cyan, "  ", "Paladin Shield: your Age of Empires IV settings are protected.");
        Console.WriteLine("  Leave this window open until the game closes.");
        Console.WriteLine();
    }

    public void Done(string message)
    {
        Console.WriteLine();
        Write(ConsoleColor.Green, "  ", message);
        Console.WriteLine();
    }

    public bool Confirm(string question, bool defaultAnswer)
    {
        if (_assumeYes)
        {
            Console.WriteLine($"  {question} [auto: yes]");
            return true;
        }
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"  {question} [no console input; using default: {(defaultAnswer ? "yes" : "no")}]");
            return defaultAnswer;
        }

        Console.Write($"  {question} [{(defaultAnswer ? "Y/n" : "y/N")}] ");
        var answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(answer)) return defaultAnswer;
        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    private static void Write(ConsoleColor colour, string prefix, string message)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = colour;
            Console.WriteLine(prefix + message);
        }
        finally { Console.ForegroundColor = previous; }
    }
}
