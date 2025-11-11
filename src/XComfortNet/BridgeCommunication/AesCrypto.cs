using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace XComfortNet;

internal sealed class AesCrypto
{
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public AesCrypto(byte[] key, byte[] iv)
    {
        _key = key;
        _iv = iv;
    }
    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        
        // Pad to AES block size (16 bytes)
        var blockSize = 16;
        var paddedLength = (plain.Length + blockSize - 1) / blockSize * blockSize;
        var paddedBytes = new byte[paddedLength];
        plain.CopyTo(paddedBytes);
        
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;

        using var enc = aes.CreateEncryptor();
        // TransformFinalBlock allocates exactly output size.
        return enc.TransformFinalBlock(paddedBytes, 0, paddedBytes.Length);
    }
    
    public string EncryptBase64(string data)
    {
        var dataBytes = Encoding.UTF8.GetBytes(data);
        
        // Pad to AES block size (16 bytes)
        var blockSize = 16;
        var paddedLength = (dataBytes.Length + blockSize - 1) / blockSize * blockSize;
        var paddedBytes = new byte[paddedLength];
        Array.Copy(dataBytes, paddedBytes, dataBytes.Length);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;

        using var encryptor = aes.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(paddedBytes, 0, paddedBytes.Length);
        
        return Convert.ToBase64String(encryptedBytes) + "\u0004";
    }

    public byte[] Decrypt(ReadOnlySpan<byte> cipher)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.Zeros;

        using var dec = aes.CreateDecryptor();
        var decrypted = dec.TransformFinalBlock(cipher.ToArray(), 0, cipher.Length).AsSpan();
        // Remove trailing null bytes
        while (decrypted.Length > 0 && decrypted[decrypted.Length-1] == 0) 
        {
            decrypted = decrypted.Slice(0, decrypted.Length-1);
        }
        return decrypted.ToArray();
    }
}
