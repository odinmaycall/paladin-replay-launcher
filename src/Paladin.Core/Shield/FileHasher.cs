using System.Security.Cryptography;

namespace Paladin.Core.Shield;

public static class FileHasher
{
    /// <summary>SHA-256 of a file's bytes, lowercase hex. Opened read-shared so a running game cannot block us.</summary>
    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
