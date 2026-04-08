using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MemShack.Application.Mining;

internal static class MiningUtilities
{
    public static string CreateDrawerId(string wing, string room, string sourceFile, int chunkIndex)
    {
        var hash = ComputeMd5Hex($"{sourceFile}{chunkIndex}")[..16];
        return $"drawer_{wing}_{room}_{hash}";
    }

    public static string NormalizeWingName(string name) =>
        name
            .ToLowerInvariant()
            .Replace(" ", "_", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal);

    public static string NowIso() =>
        DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    private static string ComputeMd5Hex(string value)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
