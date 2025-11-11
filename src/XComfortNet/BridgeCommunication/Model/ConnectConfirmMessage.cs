using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;
public class ConnectConfirm
{
    [JsonPropertyName("client_type")]
    public string ClientType { get; set; }
   
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; }
    
    [JsonPropertyName("client_version")]
    public string ClientVersion { get; set; }
    
    [JsonPropertyName("connection_id")]
    public string ConnectionId { get; set; }
}

