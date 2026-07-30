using System.Globalization;
using System.Security.Cryptography;

namespace SpreadTrader.KotakIntegration.Auth;

/// <summary>
/// RFC 6238 TOTP code generation - identical algorithm and implementation to
/// SpreadTrader.DhanIntegration.Auth.TotpGenerator (duplicated here rather than shared via a
/// project reference, matching this codebase's convention that each broker integration project
/// only ever references SpreadTrader.Core - see ARCHITECTURE.md §3.1). Used by
/// KotakAuthService.GenerateAccessTokenViaTotpAsync to compute the current 6-digit code for
/// KotakOptions.TotpSecret with no human/authenticator app involved.
///
/// Verified against the official RFC 6238 Appendix B test vector (SHA-1, 8-digit truncation,
/// ASCII secret "12345678901234567890", T=59s -&gt; "94287082") before this was wired into
/// anything that touches a real account.
/// </summary>
public static class TotpGenerator
{
    private const int Digits = 6;
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The current 6-digit TOTP code for <paramref name="base32Secret"/>, as of
    /// <paramref name="at"/> (defaults to now). Accepts the secret with or without spaces/dashes -
    /// authenticator setup pages often display it in grouped form for readability, which must not
    /// become part of the value.</summary>
    public static string GenerateCode(string base32Secret, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Secret);
        var counter = (long)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / Step.TotalSeconds);

        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation (RFC 4226 section 5.3): the low nibble of the last hash byte picks
        // a 4-byte window; the top bit of that window is dropped to keep the result positive.
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var code = binary % (int)Math.Pow(10, Digits);
        return code.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    private static byte[] Base32Decode(string input)
    {
        var cleaned = input.Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();
        if (cleaned.Length == 0)
            throw new FormatException("TOTP secret is empty.");

        var bytes = new List<byte>(cleaned.Length * 5 / 8 + 1);
        var buffer = 0;
        var bitsInBuffer = 0;

        foreach (var c in cleaned)
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException($"'{c}' is not a valid base32 character in the TOTP secret.");

            buffer = (buffer << 5) | value;
            bitsInBuffer += 5;

            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bytes.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }

        return bytes.ToArray();
    }
}
