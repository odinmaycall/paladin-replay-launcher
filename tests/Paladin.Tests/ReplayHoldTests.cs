using System.IO.Compression;
using Paladin.Core.Deep;
using static Paladin.Tests.TestHarness;

namespace Paladin.Tests;

/// <summary>
/// §888 — THE HOLD MUST OUTLIVE THE FILE.
///
/// This is the regression test for a bug that passed every check it had. §879 retained the replay by
/// PATH, after the session returned, and the launcher deletes the replay it placed in playback\ before
/// that return — so the first real capture printed "Keeping this game's replay with Paladin" and then
/// "Could not find file ...\playback\AgeIV_Replay_253128100". Nothing was wrong with the path, the
/// guard, or the upload; the bytes were simply asked for one step too late.
///
/// So the property under test is not "can it read a file" — it is THE HOLD STILL HAS THE BYTES AFTER THE
/// FILE IS GONE. The test deletes the replay, exactly as SessionRunner.CleanUp does, and then checks
/// what the hold is carrying. The launcher's own ordering cannot be tested here (this project references
/// Paladin.Core only, by design), but the thing that made the ordering wrong can be, and is.
/// </summary>
public static class ReplayHoldTests
{
    private static string WriteTempReplay(byte[] bytes)
    {
        var dir = Path.Combine(Path.GetTempPath(), "paladin-888-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "AgeIV_Replay_253128100");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Gunzip(byte[] body)
    {
        using var input = new MemoryStream(body);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static void Register()
    {
        Suite("Replay hold (888)");

        Test("the held bytes survive the file being deleted under it", () =>
        {
            // A body with real structure, so a truncated or re-read hold could not accidentally pass.
            var replay = new byte[64 * 1024];
            for (var i = 0; i < replay.Length; i++) replay[i] = (byte)(i * 31 % 251);
            var path = WriteTempReplay(replay);

            var hold = ReplayHold.From(path);
            True(hold.Ok, "a readable replay must be held");

            // THE POINT OF ALL THIS: the launcher now deletes it, as it always did.
            File.Delete(path);
            False(File.Exists(path), "the file must really be gone for this test to mean anything");

            True(hold.Ok, "the hold must not depend on the file it came from");
            Equal(replay.Length, hold.RawBytes, "the raw size is what the console reports");
            True(Gunzip(hold.Body!).AsSpan().SequenceEqual(replay), "and the bytes must be the replay's own, byte for byte");

            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        });

        Test("a replay that is already gone is a described failure, not an exception", () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), "paladin-888-absent", "AgeIV_Replay_1");
            var hold = ReplayHold.From(missing);

            False(hold.Ok, "there are no bytes to hold");
            True(hold.Error is not null, "and the console must be told why");
            True(hold.Error!.Contains("could not be read"), $"phrased for a reader, not a stack trace: {hold.Error}");
            Equal(0, hold.RawBytes, "nothing was read");
        });

        Test("an empty replay is held as empty rather than refused", () =>
        {
            // Not a hypothetical: a provider that writes the file before filling it would produce this,
            // and the Worker — not the launcher — is what decides a body is not a replay (§879).
            var path = WriteTempReplay(Array.Empty<byte>());
            var hold = ReplayHold.From(path);

            True(hold.Ok, "a read that succeeded is a hold that succeeded");
            Equal(0, hold.RawBytes, "and it honestly reports nothing in it");
            Equal(0, Gunzip(hold.Body!).Length, "gzip of nothing decompresses to nothing");

            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        });

        Test("the compressed body is what goes on the wire, and it is smaller", () =>
        {
            // The median banked replay is 289 KiB compressed; the point of compressing at hold time is
            // that the size check (WireCapBytes) happens while the file still exists to be re-read.
            var replay = new byte[512 * 1024];   // highly compressible, like a real replay's order stream
            var path = WriteTempReplay(replay);
            var hold = ReplayHold.From(path);

            True(hold.Ok, "held");
            True(hold.Body!.Length < replay.Length, $"{hold.Body.Length} must be under {replay.Length}");
            True(hold.Body.Length < ReplayUploader.WireCapBytes, "and inside Paladin's cap");
            Equal(replay.Length, hold.RawBytes, "while the raw size is still reported truthfully");

            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        });
    }
}
