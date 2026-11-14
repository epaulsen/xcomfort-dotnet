using System.Buffers;
using System.Buffers.Text;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using XComfortDotNet;
using XComfortDotNet.Model;

namespace XComfortNet;

public class EncryptedSocket(ClientWebSocket socket, string deviceId, byte[] key, byte[] iv, ILogger? logger = null)
    : IXComfortWebSocket
{
    private readonly AesCrypto _crypto = new(key, iv);

    public async Task SendAsync(XComfortMessage message, CancellationToken cancellationToken = default)
    {
        var plain = message.ToUtf8Bytes();
        BridgeMessageLogger.Log(logger, "Sending", message, deviceId);
        await SendInternalAsync(plain, cancellationToken);
    }

    public async Task SendAsync<T>(Messages type, T message, CancellationToken cancellationToken = default)
    {
        var msg = new XComfortMessage<T>(type, message);
        var plain = JsonSerializer.SerializeToUtf8Bytes(msg); // JSON as bytes
        BridgeMessageLogger.Log(logger, "Sending", msg, deviceId);
        await SendInternalAsync(plain, cancellationToken);
    }

    internal class Ack
    {
        [JsonPropertyName("type_int")]
        public int Type => 1;

        [JsonPropertyName("ref")]
        public int Ref { get; set; }
    }

    public async Task SendAckAsync(int messageId, CancellationToken cancellationToken = default)
    {
        var ack = new Ack() { Ref = messageId };
        var plain = JsonSerializer.SerializeToUtf8Bytes(ack); // JSON as bytes
        logger?.LogDebug("Sending xComfort Bridge acknowledgement for message {MessageId} to bridge {BridgeId}",
            messageId, deviceId);
        await SendInternalAsync(plain, cancellationToken);
    }

    private async Task SendInternalAsync(byte[] plainBytes, CancellationToken cancellationToken = default)
    {
        var encrypted = _crypto.Encrypt(plainBytes);               // Encrypt payload

        int maxLen = Base64.GetMaxEncodedToUtf8Length(encrypted.Length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxLen + 1); // +1 for EOT (0x04)
        try
        {
            var status = Base64.EncodeToUtf8(encrypted, buffer, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != encrypted.Length)
                throw new CryptographicException($"Base64 encode failed (status {status}, consumed {consumed}, written {written}).");

            buffer[written] = 0x04; // Append EOT
            await socket.SendAsync(buffer.AsMemory(0, written + 1), WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<XComfortMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        // Bytes received are UTF-8 Base64 text representing the encrypted payload.
        var encoded = await socket.ReceiveStreamAsync(cancellationToken, logger);

        var maxDecoded = Base64.GetMaxDecodedFromUtf8Length(encoded.Length);
        byte[] decodedBuffer = ArrayPool<byte>.Shared.Rent(maxDecoded);
        try
        {
            var status = Base64.DecodeFromUtf8(encoded, decodedBuffer, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != encoded.Length)
                throw new FormatException($"Invalid Base64 payload (status {status}, consumed {consumed}, written {written}).");

            var decrypted = _crypto.Decrypt(decodedBuffer.AsSpan(0, written));

            var message = JsonSerializer.Deserialize<XComfortMessage>(decrypted)
                ?? throw new JsonException("Empty bridge message.");
            BridgeMessageLogger.Log(logger, "Received", message, deviceId);
            return message;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(decodedBuffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}