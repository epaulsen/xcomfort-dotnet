using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class SecretPayload
{
    [JsonPropertyName("secret")]
    public string Secret { get; set; }
}