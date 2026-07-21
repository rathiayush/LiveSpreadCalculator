using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LiveSpreadCalculator.Auth;

/// <summary>Mints signed JWTs for users who have passed CsvUserStore validation. HMAC-SHA256 with
/// the shared secret from JwtOptions.Key - the same key the JwtBearer middleware validates against
/// in Program.cs, so a token this service issues is exactly what the API will accept.</summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key));
    }

    /// <summary>Issues a token identifying the given user, expiring ExpiryDays from now. Returns the
    /// compact JWT string and the absolute UTC expiry so the caller can hand both to the client.
    ///
    /// <paramref name="securityStamp"/> is a fingerprint of the user's current password (see
    /// CsvUserStore.GetSecurityStamp). It's carried as the "stamp" claim and re-validated on every
    /// request in Program.cs, so changing the password moves the stamp and instantly invalidates
    /// every token that was issued against the old one - i.e. a forced logout on all devices.</summary>
    public (string Token, DateTime ExpiresUtc) CreateToken(string email, string securityStamp)
    {
        var expires = DateTime.UtcNow.AddDays(_options.ExpiryDays);

        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, email),
                new Claim(ClaimTypes.Name, email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("stamp", securityStamp),
            }),
            Expires = expires,
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return (token, expires);
    }
}
