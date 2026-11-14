using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.OpenSsl;
using XComfortDotNet.Model;
using XComfortNet;

namespace XComfortDotNet;

internal static class XComfortBridgeFactory
{
    public static async Task<MessagePump> ConnectAsync(
        string ipAddress, string authKey, ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{ipAddress}/"), cancellationToken);
            var bridgeLogger = loggerFactory?.CreateLogger("XComfortNet.Bridge");
            IXComfortWebSocket comm = new PlainTextSocket(socket, bridgeLogger);

            var msg = await comm.ReceiveAsync(cancellationToken);
            ArgumentNullException.ThrowIfNull(msg);

            if (msg.TypeId != Messages.CONNECTION_START)
            {
                throw new Exception($"Unable to proceed got unexpected message with type_id: '{msg.TypeId}'");
            }
            var connectionStart = msg as XComfortMessage<ConnectionStart>
                ?? throw new InvalidOperationException("Invalid connection start payload.");
            var deviceId = connectionStart.Payload.DeviceId;
            var connectionId = connectionStart.Payload.ConnectionId;

            await comm.SendAsync(Messages.CONNECTION_CONFIRM, new ConnectConfirm()
            {
                ConnectionId = connectionId,
                ClientType = "shl-app",
                ClientVersion = "3.0.0",
                ClientId = "c956e43f999f8004"
            }, cancellationToken: cancellationToken);

            var reply = await comm.ReceiveAsync(cancellationToken: cancellationToken);
            if (reply?.TypeId == Messages.CONNECTION_DECLINED)
            {
                throw new Exception("Gateway refused connection");
            }

            // Init secure comm
            await comm.SendAsync(new XComfortMessage(Messages.SC_INIT), cancellationToken);

            msg = await comm.ReceiveAsync(cancellationToken: cancellationToken);
            ArgumentNullException.ThrowIfNull(msg);
            var pubKeyMsg = msg as XComfortMessage<PublicKey>
                ?? throw new InvalidOperationException("Invalid public key payload.");
            var publicKeyPem = pubKeyMsg.Payload.Key;

            // Parse RSA public key
            var pemReader = new PemReader(new StringReader(publicKeyPem));
            var publicKey = (AsymmetricKeyParameter)pemReader.ReadObject();

            // Generate AES key and IV
            var key = new byte[32];
            var iv = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(key);
                rng.GetBytes(iv);
            }

            // Encrypt key and IV with RSA
            var cipher = new Pkcs1Encoding(new RsaEngine());
            cipher.Init(true, publicKey);

            var keyIvString = BitConverter.ToString(key).Replace("-", "").ToLower() + ":::" +
                              BitConverter.ToString(iv).Replace("-", "").ToLower();
            var keyIvBytes = Encoding.UTF8.GetBytes(keyIvString);
            var encryptedSecret = cipher.ProcessBlock(keyIvBytes, 0, keyIvBytes.Length);
            var secret = Convert.ToBase64String(encryptedSecret);

            // Send encrypted secret
            await comm.SendAsync(Messages.SC_SECRET, new SecretPayload() { Secret = secret }, cancellationToken: cancellationToken);

            // From here on, all communication is encrypted.
            comm = new EncryptedSocket(socket, deviceId, key, iv, bridgeLogger);

            msg = await comm.ReceiveAsync(cancellationToken);
            if (msg.TypeId != Messages.SC_ESTABLISHED)
            {
                throw new Exception("Unable to establish secure connection");
            }
            var salt = GenerateSalt();
            var hash = Hash(deviceId, authKey, salt);

            await comm.SendAsync(Messages.AUTH_LOGIN, new LoginRequest()
            {
                Username = "default",
                Password = hash,
                Salt = salt
            }, cancellationToken);

            msg = await comm.ReceiveAsync(cancellationToken);

            if (msg.TypeId != Messages.AUTH_LOGIN_SUCCESS)
            {
                throw new Exception("Login failed");
            }

            var token = msg.AdditionalData?["payload"].GetProperty("token").GetString() ?? throw new Exception("Token not found");
            await comm.SendAsync(Messages.AUTH_APPLY_TOKEN, new TokenMessage() { Token = token }, cancellationToken);

            // Receive token validation
            msg = await comm.ReceiveAsync(cancellationToken);
            ValidateToken(msg);

            // Renew token
            await comm.SendAsync(Messages.AUTH_RENEW_TOKEN, new TokenMessage() { Token = token }, cancellationToken);

            msg = await comm.ReceiveAsync(cancellationToken);
            if (msg.TypeId != Messages.AUTH_RENEW_TOKEN_RESPONSE)
            {
                throw new Exception("Token renewal failed");
            }

            token = msg.AdditionalData?["payload"].GetProperty("token").GetString() ?? throw new Exception("Token not found");
            await comm.SendAsync(Messages.AUTH_APPLY_TOKEN, new TokenMessage() { Token = token }, cancellationToken);

            msg = await comm.ReceiveAsync(cancellationToken);
            ValidateToken(msg);

            return new MessagePump(comm, deviceId, loggerFactory?.CreateLogger<MessagePump>(),
                connectionStart.Payload.DeviceVersion);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void ValidateToken(XComfortMessage message)
    {
        if (message.TypeId != Messages.AUTH_APPLY_TOKEN_RESPONSE ||
            message.AdditionalData?["payload"].GetProperty("valid").GetBoolean() != true)
        {
            throw new InvalidOperationException("Bridge rejected the session token.");
        }
    }

    private static string GenerateSalt()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, 12)
            .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)])
            .ToArray());
    }

    private static string Hash(string deviceId, string authKey, string salt)
    {
        using var sha256 = SHA256.Create();

        // First hash: SHA256(deviceId + authKey)
        var firstInput = Encoding.UTF8.GetBytes(deviceId + authKey);
        var firstHash = sha256.ComputeHash(firstInput);
        var firstHashHex = BitConverter.ToString(firstHash).Replace("-", "").ToLower();

        // Second hash: SHA256(salt + firstHashHex)
        var secondInput = Encoding.UTF8.GetBytes(salt + firstHashHex);
        var secondHash = sha256.ComputeHash(secondInput);

        return BitConverter.ToString(secondHash).Replace("-", "").ToLower();
    }
}