using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetCompare.Core;

public static class Hashing
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Fingerprint(object value) =>
        Sha256(JsonSerializer.Serialize(value, JsonOptions));

    public static string StableId(string prefix, params string[] parts) =>
        prefix + "_" + Sha256(string.Join("\u001f", parts)).Substring(0, 16);
}
