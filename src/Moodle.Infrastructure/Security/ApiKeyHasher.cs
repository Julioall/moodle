using System.Security.Cryptography;
using System.Text;

namespace Moodle.Infrastructure;

internal static class ApiKeyHasher
{
    public static string Hash(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(apiKey);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}