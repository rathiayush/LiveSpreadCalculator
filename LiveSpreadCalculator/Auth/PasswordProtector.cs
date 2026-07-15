using System.Security.Cryptography;
using System.Text;

namespace LiveSpreadCalculator.Auth;

/// <summary>Reversible, key-based password protection for App_Data/users.csv.
///
/// IMPORTANT design note: the user asked for a "hash on the basis of a key" AND the ability to see
/// the decrypted password. A true hash (SHA-256, bcrypt, ...) is ONE-WAY and can never be reversed,
/// so it cannot satisfy "show me the original password". What actually gives you both properties is
/// symmetric ENCRYPTION with a secret key - that's what this class does (AES-256-CBC). Anyone (this
/// app, or the PowerShell script in Tools/Decrypt-Users.ps1) holding the same key can decrypt and
/// read the original password; anyone without it sees only ciphertext.
///
/// Wire format of an encrypted field:  "enc:" + Base64( IV(16 bytes) || ciphertext )
///   - Key      = SHA-256(UTF8(secret))            -> 32 bytes, so AES-256
///   - IV       = 16 random bytes, fresh per encrypt, stored as the first 16 bytes of the blob
///   - Cipher   = AES / CBC / PKCS7
/// The PowerShell decrypt script reproduces this exact scheme, so the two stay interoperable.</summary>
public sealed class PasswordProtector
{
    /// <summary>Marker that tells an encrypted CSV field apart from a legacy plaintext one.</summary>
    public const string Prefix = "enc:";

    private readonly byte[] _key;

    public PasswordProtector(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Password encryption key must be configured (Security:PasswordKey).", nameof(secret));

        // Derive a fixed 32-byte AES-256 key from whatever length the configured secret is.
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>True if the stored field is one this class produced (i.e. carries the enc: marker).</summary>
    public bool IsEncrypted(string? storedValue) =>
        storedValue is not null && storedValue.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts a plaintext password into the "enc:..." wire format above.</summary>
    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        var iv = aes.IV;
        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var combined = new byte[iv.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(cipher, 0, combined, iv.Length, cipher.Length);

        return Prefix + Convert.ToBase64String(combined);
    }

    /// <summary>Decrypts an "enc:..." field back to the original password. A field that isn't
    /// encrypted (legacy plaintext) is returned unchanged, so this is safe to call on any row.</summary>
    public string Decrypt(string storedValue)
    {
        if (!IsEncrypted(storedValue))
            return storedValue; // legacy plaintext - nothing to do

        var combined = Convert.FromBase64String(storedValue[Prefix.Length..]);

        var iv = new byte[16];
        Buffer.BlockCopy(combined, 0, iv, 0, 16);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(combined, 16, combined.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Short, stable fingerprint of the STORED password field (whatever is on disk -
    /// plaintext or the enc: blob). It is embedded into a user's JWT as the "stamp" claim at login
    /// and re-checked on every request. Because a password change rewrites the stored field (a new
    /// random IV alone guarantees a different blob), the stamp changes too - which instantly makes
    /// every previously issued token for that user invalid, forcing a logout on all devices.</summary>
    public static string StampFor(string? storedPasswordField)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(storedPasswordField ?? string.Empty));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant(); // 16 hex chars is plenty
    }
}
