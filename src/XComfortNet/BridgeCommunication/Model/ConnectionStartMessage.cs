using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class ConnectionStart
{
    [JsonPropertyName("device_id")]
    public required string DeviceId { get; set; }

    [JsonPropertyName("device_version")]
    public required string DeviceVersion { get; set; }

    [JsonPropertyName("connection_id")]
    public required string ConnectionId { get; set; }
}