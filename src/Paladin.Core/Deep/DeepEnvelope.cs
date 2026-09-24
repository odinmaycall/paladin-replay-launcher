using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Paladin.Core.Deep;

/// <summary>
/// §867 — what a Deep capture sends: `{"v":1,"gameId":&lt;id&gt;,"rows":"&lt;every evidence line&gt;"}`, gzipped.
///
/// GZIP IS NOT AN OPTIMISATION HERE, IT IS THE ONLY WAY THE PAYLOAD FITS. The production cadence is one
/// sample every five game-seconds, which triples the rows a capture carries, and four of the six real
/// cadence-5 captures measured are over the Worker's 1 MiB body cap as plain JSON — the largest is
/// 1,277 KiB. The same captures compress 16.8-17.1x, so the worst of them goes to 74.7 KiB. A launcher
/// that sent this uncompressed would meet a 413 on most games it captured.
///
/// The Worker accepts either form and decides by the body's own first two bytes rather than by the
/// header, so an uncompressed send still works — but there is no reason to make one.
/// </summary>
public sealed record DeepEnvelope(long GameId, string Rows)
{
    /// <summary>The envelope version the Worker reads. It refuses anything else outright.</summary>
    public const int Version = 1;

    /// <summary>The JSON as it would go on the wire uncompressed. Kept for the on-disk copy.</summary>
    public string ToJson() => JsonSerializer.Serialize(new EnvelopeShape(Version, GameId, Rows));

    /// <summary>The bytes actually sent: the JSON, gzipped at the best ratio the runtime offers.</summary>
    public byte[] ToGzip()
    {
        var json = Encoding.UTF8.GetBytes(ToJson());
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(json, 0, json.Length);
        return output.ToArray();
    }

    /// <summary>How big this is uncompressed, which is what the Worker's text cap applies to.</summary>
    public int JsonBytes => Encoding.UTF8.GetByteCount(ToJson());

    /// <summary>
    /// Build from the evidence lines a capture kept. The rows go in newline-joined and otherwise
    /// untouched, because the site's parser finds `PALADIN5_VA|` inside each line rather than anchoring
    /// it — the game's own clock prefix is part of the evidence and is not stripped here.
    /// </summary>
    public static DeepEnvelope From(long gameId, IEnumerable<string> lines) =>
        new(gameId, string.Join("\n", lines));

    private sealed record EnvelopeShape(int v, long gameId, string rows);
}
