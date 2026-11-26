using System.Security.Cryptography;

namespace Mbr.Core.Security;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
