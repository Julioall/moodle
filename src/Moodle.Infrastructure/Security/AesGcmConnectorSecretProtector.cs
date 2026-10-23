using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Moodle.Infrastructure;

internal sealed class AesGcmConnectorSecretProtector(IOptions<ConnectorSecretsOptions> options) : IConnectorSecretProtector
{
    private readonly byte[] _key = ResolveKey(options.Value.EncryptionKeyBase64);

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintextBytes, cipher, tag);

        var payload = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string ciphertext)
    {
        if (string.IsNullOrWhiteSpace(ciphertext))
        {
            return string.Empty;
        }

        var payload = Convert.FromBase64String(ciphertext);
        if (payload.Length < 28)
        {
            throw new InvalidOperationException("Segredo criptografado invalido.");
        }

        var nonce = payload[..12];
        var tag = payload[12..28];
        var cipher = payload[28..];

        var plaintext = new byte[cipher.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, cipher, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] ResolveKey(string keyBase64)
    {
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            throw new InvalidOperationException("ConnectorSecrets:EncryptionKeyBase64 nao configurado.");
        }

        var key = Convert.FromBase64String(keyBase64);
        if (key.Length != 32)
        {
            throw new InvalidOperationException("ConnectorSecrets:EncryptionKeyBase64 deve conter 32 bytes (AES-256).", new ArgumentException(nameof(keyBase64)));
        }

        return key;
    }
}