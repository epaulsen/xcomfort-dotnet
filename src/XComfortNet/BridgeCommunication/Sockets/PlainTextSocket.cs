using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

public class PlainTextSocket(ClientWebSocket socket, ILogger? logger = null) : IXComfortWebSocket
{
    public async Task SendAsync(XComfortMessage message, CancellationToken cancellationToken = default)
    {
        BridgeMessageLogger.Log(logger, "Sending", message);
        await socket.SendAsync(message.ToUtf8Bytes(), WebSocketMessageType.Text, true,
            cancellationToken);
    }

    public async Task SendAsync<T>(Messages type, T message, CancellationToken cancellationToken = default)
    {
        var msg = new XComfortMessage<T>(type, message);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg);
        BridgeMessageLogger.Log(logger, "Sending", msg);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    public Task SendAckAsync(int messageId, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<XComfortMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await socket.ReceiveStreamAsync(cancellationToken, logger);
        
        var msg = JsonSerializer.Deserialize<JsonElement>(bytes);
        int typeInt = msg.GetProperty("type_int").GetInt32();
        var message = (Messages)typeInt switch
        {
            Messages.CONNECTION_START => JsonSerializer.Deserialize<XComfortMessage<ConnectionStart>>(bytes)!,
            Messages.CONNECTION_CONFIRM => JsonSerializer.Deserialize<XComfortMessage<ConnectConfirm>>(bytes)!,
            Messages.SC_PUBKEY => JsonSerializer.Deserialize<XComfortMessage<PublicKey>>(bytes)!,

            _ => JsonSerializer.Deserialize<XComfortMessage>(bytes)!
        };
        BridgeMessageLogger.Log(logger, "Received", message);
        return message;
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}