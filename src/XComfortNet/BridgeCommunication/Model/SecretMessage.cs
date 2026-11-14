using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class SecretPayload
{
    [JsonPropertyName("secret")]
    public required string Secret { get; set; }
}