using System.Text.Json;
using System.Text.Json.Serialization;

namespace XComfortDotNet.Model;

public class XComfortMessage
{
    public XComfortMessage()
    {
        
    }

    public XComfortMessage(Messages typeId)
    {
        TypeId = typeId;
    }
    
    [JsonPropertyName("type_int")]
    [JsonPropertyOrder(1)]
    public Messages TypeId { get; set; }

    [JsonPropertyName("mc")]
    [JsonPropertyOrder(2)]    
    public int MessageCounter { get; set; } = -1;
    
    [JsonPropertyName("ref")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(3)]
    public int? MessageReference { get; set; }
    
    // Holds any extra JSON properties not mapped to explicit members.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public byte[] ToUtf8Bytes() => JsonSerializer.SerializeToUtf8Bytes(this, GetType());
}

public class XComfortMessage<T> : XComfortMessage
{
    public XComfortMessage()
    {
        
    }

    public XComfortMessage(Messages messageType, T payload): base(messageType)
    {
        Payload = payload;
    }
    
    [JsonPropertyName("payload")]
    [JsonPropertyOrder(4)]    
    public T Payload { get; set; } = default!;
}