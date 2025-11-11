using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class PublicKey
{
    [JsonPropertyName("public_key")]
    public string Key { get; set; }
}