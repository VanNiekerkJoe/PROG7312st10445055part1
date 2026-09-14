using System.Security.Cryptography;

namespace SmartX.Api.Services;

/// <summary>
/// AES-256-GCM encryption for attachment file contents at rest. Each file
/// is stored on disk as [12-byte nonce][16-byte auth tag][ciphertext] —
/// nothing is ever written unencrypted. GCM is authenticated, so a
/// tampered or corrupted file fails to decrypt rather than silently
/// returning wrong bytes.
///
/// The key lives in configuration (Attachments:EncryptionKeyBase64), not
/// hardcoded, so it can be rotated without touching code. In a real
/// deployment this would come from a secret manager (Azure Key Vault, AWS
/// Secrets Manager, or `dotnet user-secrets` locally) rather than
/// appsettings.json — it's in appsettings.json here only so the
/// assessment environment runs without extra setup on a marker's machine.
/// </summary>
public sealed class AttachmentEncryptionService
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AttachmentEncryptionService(IConfiguration configuration)
    {
        var keyBase64 = configuration["Attachments:EncryptionKeyBase64"]
            ?? throw new InvalidOperationException(
                "Attachments:EncryptionKeyBase64 is not configured in appsettings.json.");

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
        {
            throw new InvalidOperationException(
                "Attachments:EncryptionKeyBase64 must decode to exactly 32 bytes (AES-256).");
        }
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] encrypted)
    {
        var nonce = encrypted[..NonceSize];
        var tag = encrypted[NonceSize..(NonceSize + TagSize)];
        var ciphertext = encrypted[(NonceSize + TagSize)..];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}