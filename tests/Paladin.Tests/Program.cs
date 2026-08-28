using Paladin.Tests;

Console.WriteLine();
Console.WriteLine("  Paladin Replay Launcher — test suite");
Console.WriteLine("  " + new string('-', 46));

ShieldTests.Register();
SessionTests.Register();
ReplayAndLaunchTests.Register();

return TestHarness.Run();
