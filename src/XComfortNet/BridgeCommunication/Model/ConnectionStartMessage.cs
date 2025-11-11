using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class ConnectionStart
{
    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; }

    [JsonPropertyName("device_version")]
    public string DeviceVersion { get; set; }

    [JsonPropertyName("connection_id")]
    public string ConnectionId { get; set; }
}