using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class ConnectConfirm
{
    [JsonPropertyName("client_type")]
    public required string ClientType { get; set; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; set; }

    [JsonPropertyName("client_version")]
    public required string ClientVersion { get; set; }

    [JsonPropertyName("connection_id")]
    public required string ConnectionId { get; set; }
}

