using System.Security.Cryptography;
using System.Text;

namespace Plooto.Ap.Infrastructure.Security;

/// <summary>API keys are random 128-bit hex strings; only their SHA-256 hash is persisted.</summary>
public static class ApiKey
{
    public static string Generate() => RandomNumberGenerator.GetHexString(32).ToLowerInvariant();

    public static string Hash(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))).ToLowerInvariant();
}
