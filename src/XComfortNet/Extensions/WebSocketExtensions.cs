using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XComfortDotNet.Model;

namespace XComfortDotNet;

internal static class JSonExtension
{
    public static Messages GetMessageType(this JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected object");
        }

        if (!element.TryGetProperty("type_id", out var typeProp))
        {
            throw new JsonException("Property 'type_id' not found");
        }
        
        return (Messages)typeProp.GetInt32();
    }
}

internal static class WebSocketExtensions
{
    public static async Task<XComfortMessage<T>?> ReceiveMessageAsync<T>(this ClientWebSocket socket,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReceiveStreamAsync(socket, cancellationToken);
        return JsonSerializer.Deserialize<XComfortMessage<T>>(bytes);
    }

    public static async Task<XComfortMessage?> ReceiveMessageAsync(this ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var bytes = await ReceiveStreamAsync(socket, cancellationToken);
        return JsonSerializer.Deserialize<XComfortMessage>(bytes);
    }

    internal static async Task<byte[]> ReceiveStreamAsync(this ClientWebSocket socket, CancellationToken cancellationToken, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8192); // 8 KB buffer
        
        try
        {
            while (true)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await socket.ReceiveAsync(segment, cancellationToken);

                ms.Write(buffer, 0, result.Count);

                if (result.EndOfMessage)
                {
                    var array = ms.ToArray();
                    if (array[^1] == 0x04 || array[^1] == 0x00)  // Remove trailing null character if present
                    {
                        array = array.SkipLast(1).ToArray();
                    }
                    logger.LogDebug("Received raw: {raw}", Encoding.UTF8.GetString(array));
                    return array;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", cancellationToken);
                    return [];
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer); // Returner buffer til pool
        }
    }

    public static async Task SendMessageAsync(this ClientWebSocket socket, XComfortMessage message, CancellationToken cancellationToken = default)
    {
        using var memoryStream = new MemoryStream();
        await JsonSerializer.SerializeAsync(memoryStream, message, cancellationToken: cancellationToken);
        memoryStream.Seek(0, SeekOrigin.Begin);
        
        await socket.SendAsync(new ArraySegment<byte>(memoryStream.ToArray()), WebSocketMessageType.Text, true, cancellationToken);
    }
}


internal interface ICrypto
    {
        ReadOnlySpan<char> Encrypt(ReadOnlySpan<char> plainText);
        ReadOnlySpan<char> Decrypt(ReadOnlySpan<char> cipherText);
    }

    internal class AesEncryption(byte[] key, byte[] iv) : ICrypto
    {
        private static char[] _terminator = "\u0004".ToCharArray();
    
        public ReadOnlySpan<char> Encrypt(ReadOnlySpan<char> plainText)
        {
            var dataBytes = plainText.ToArray();
        
            // Pad to AES block size (16 bytes)
            var blockSize = 16;
            var paddedLength = (dataBytes.Length + blockSize - 1) / blockSize * blockSize;
            var paddedBytes = new byte[paddedLength];
            Array.Copy(dataBytes, paddedBytes, dataBytes.Length);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.Zeros;

            using var encryptor = aes.CreateEncryptor();
            var encryptedBytes = encryptor.TransformFinalBlock(paddedBytes, 0, paddedBytes.Length);
        
            char[] chars = new char[Encoding.UTF8.GetCharCount(encryptedBytes, 0, encryptedBytes.Length)+2];
        
            Encoding.UTF8.GetChars(encryptedBytes, chars);
            return chars.Concat(_terminator).ToArray().AsSpan();
        }

        public ReadOnlySpan<char>Decrypt(ReadOnlySpan<char> cipherText)
        {
            var cipherBytes = Convert.FromHexString(cipherText);
        
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.Zeros;
        
            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
            var endIndex = Array.FindLastIndex(decryptedBytes, b => b != 0) + 1;
            if (endIndex <= 0)
            {
                return "{}";
            }
        
            char[] chars = new char[Encoding.UTF8.GetCharCount(decryptedBytes, 0, endIndex)];
            Encoding.UTF8.GetChars(decryptedBytes, chars);
            return chars.AsSpan();
        }
    }

    internal class PlainText : ICrypto
    {
        public ReadOnlySpan<char> Encrypt(ReadOnlySpan<char> plainText)
        {
            return plainText;
        }

        public ReadOnlySpan<char> Decrypt(ReadOnlySpan<char> cipherText)
        {
            return cipherText;
        }
    }