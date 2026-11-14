using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class PublicKey
{
    [JsonPropertyName("public_key")]
    public required string Key { get; set; }
}