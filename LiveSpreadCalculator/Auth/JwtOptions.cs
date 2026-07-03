namespace LiveSpreadCalculator.Auth;

/// <summary>Binds the "Jwt" section of appsettings.json - see Program.cs. Key must be overridden
/// with a real secret via environment variable (Jwt__Key) or user-secrets in any non-local
/// deployment; the committed value is a dev default only.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "LiveSpreadCalculator";
    public string Audience { get; set; } = "LiveSpreadCalculator.Dashboard";

    /// <summary>How long an issued token stays valid. Requested 2-3 days; defaults to 3.</summary>
    public int ExpiryDays { get; set; } = 3;
}
