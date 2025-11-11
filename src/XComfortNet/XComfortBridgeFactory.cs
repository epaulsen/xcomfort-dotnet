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

public class XComfortBridgeFactory(string ipAddress, string authKey, ILoggerFactory? loggerFactory = null)
{
    private readonly ClientWebSocket _socket = new();
    private IXComfortWebSocket _comm;
    
    public async Task<XComfortBridge> CreateBridgeAsync(CancellationToken cancellationToken = default)
    {
        await _socket.ConnectAsync(new Uri($"ws://{ipAddress}/"), cancellationToken);
        _comm = new PlainTextSocket(_socket, loggerFactory?.CreateLogger<PlainTextSocket>());
        
        var msg = await _comm.ReceiveAsync(cancellationToken);
        ArgumentNullException.ThrowIfNull(msg);
        
        if (msg.TypeId != Messages.CONNECTION_START)
        {
            throw new Exception($"Unable to proceed got unexpected message with type_id: '{msg.TypeId}'");
        }
        var connectionStart = msg as XComfortMessage<ConnectionStart>;
        var deviceId = connectionStart.Payload.DeviceId;
        var connectionId = connectionStart.Payload.ConnectionId;

        await _comm.SendAsync(Messages.CONNECTION_CONFIRM, new ConnectConfirm()
            {
                ConnectionId = connectionId,
                ClientType = "shl-app",
                ClientVersion = "3.0.0",
                ClientId = "c956e43f999f8004"
            }, cancellationToken: cancellationToken);
        
        var reply = await _comm.ReceiveAsync(cancellationToken: cancellationToken);
        if (reply?.TypeId == Messages.CONNECTION_DECLINED)
        {
            throw new Exception("Gateway refused connection");
        }
        
        // Init secure comm
        await _comm.SendAsync(new XComfortMessage(Messages.SC_INIT));
        
        msg = await _comm.ReceiveAsync(cancellationToken: cancellationToken);
        ArgumentNullException.ThrowIfNull(msg);
        var pubKeyMsg = msg as XComfortMessage<PublicKey>;
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
        await _comm.SendAsync(Messages.SC_SECRET,new SecretPayload() {Secret = secret}, cancellationToken: cancellationToken);

        // From here on, all communication is encrypted.
        _comm = new EncryptedSocket(_socket, deviceId, key, iv, loggerFactory?.CreateLogger<EncryptedSocket>());
        
        msg = await _comm.ReceiveAsync(cancellationToken);
        if (msg.TypeId != Messages.SC_ESTABLISHED)
        {
            throw new Exception("Unable to establish secure connection");
        }
        var salt = GenerateSalt();
        var hash = Hash(deviceId, authKey, salt);

        await _comm.SendAsync(Messages.AUTH_LOGIN, new LoginRequest()
        {
            Username = "default",
            Password = hash,
            Salt = salt
        }, cancellationToken);

        msg = await _comm.ReceiveAsync(cancellationToken);
        
        if (msg.TypeId != Messages.AUTH_LOGIN_SUCCESS)
        {
            throw new Exception("Login failed");
        }
        
        var token = msg.AdditionalData?["payload"].GetProperty("token").GetString() ?? throw new Exception("Token not found");
        await _comm.SendAsync(Messages.AUTH_APPLY_TOKEN, new TokenMessage() { Token = token }, cancellationToken);
        
        // Receive token validation
        msg = await _comm.ReceiveAsync(cancellationToken);

        // Renew token
        await _comm.SendAsync(Messages.AUTH_RENEW_TOKEN, new TokenMessage() { Token = token }, cancellationToken);
        
        msg = await _comm.ReceiveAsync(cancellationToken);
        if (msg.TypeId != Messages.AUTH_RENEW_TOKEN_RESPONSE)
        {
            throw new Exception("Token renewal failed");
        }
        
        token = msg.AdditionalData?["payload"].GetProperty("token").GetString() ?? throw new Exception("Token not found");
        await _comm.SendAsync(Messages.AUTH_APPLY_TOKEN, new TokenMessage() { Token = token }, cancellationToken);
        
        msg = await _comm.ReceiveAsync(cancellationToken);
        
        return new XComfortBridge(new MessagePump(_comm), loggerFactory);
    }
    
    private static string GenerateSalt()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var random = new Random();
        return new string(Enumerable.Range(0, 12)
            .Select(_ => chars[random.Next(chars.Length)])
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