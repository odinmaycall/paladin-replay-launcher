using Paladin.Tests;

Console.WriteLine();
Console.WriteLine("  Paladin Replay Launcher — test suite");
Console.WriteLine("  " + new string('-', 46));

ShieldTests.Register();
SessionTests.Register();
ReplayAndLaunchTests.Register();
PathDisplayTests.Register();
DumpTests.Register();
DumpRunTests.Register();
DeepEvidenceTests.Register();
DeepLadderTests.Register();
DeepSelfCheckTests.Register();
DeepEnvelopeTests.Register();
DeepPreflightTests.Register();
DeepCommandTests.Register();

return TestHarness.Run();
