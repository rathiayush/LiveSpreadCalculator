using System.Collections.Concurrent;
using System.Threading;

namespace LiveSpreadCalculator.Auth;

/// <summary>Validates app-login credentials against a small CSV file (email,password - plaintext,
/// one user per line, header row required). This is the allow-list of who is permitted to obtain a
/// JWT and therefore call the API at all; anyone not in the file cannot log in. Same "a CSV file is
/// a deliberate stopgap, not a real database" spirit as CsvTriggerRuleStore.
///
/// The file is re-read on every login attempt (cheap - it's a handful of lines) so adding or
/// removing a user takes effect immediately without restarting the app. Passwords are stored in
/// plaintext by design choice for this private/local tool; keep App_Data/users.csv out of source
/// control and off any shared location.</summary>
public sealed class CsvUserStore
{
    private readonly string _csvPath;
    private readonly ILogger<CsvUserStore> _logger;

    public CsvUserStore(string csvPath, ILogger<CsvUserStore> logger)
    {
        _csvPath = csvPath;
        _logger = logger;
    }

    /// <summary>Returns true only when the email exists in the CSV and the password matches exactly.
    /// Email comparison is case-insensitive; password comparison is exact (case-sensitive).</summary>
    public bool ValidateCredentials(string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return false;

        foreach (var (fileEmail, filePassword) in LoadUsers())
        {
            if (string.Equals(fileEmail, email.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(filePassword, password))
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<(string Email, string Password)> LoadUsers()
    {
        if (!File.Exists(_csvPath))
        {
            _logger.LogWarning("Users CSV not found at {Path} - no one can log in until it exists.", _csvPath);
            yield break;
        }

        // Retried a few times with a short backoff, same as CsvTriggerRuleStore's WithRetry: on
        // Windows/Azure App Service, App_Data\users.csv can be transiently locked by an antivirus
        // scanner, a sync client, the search indexer, or (in this case) another request/process
        // that has the file briefly open. Without a retry, that transient lock throws here, gets
        // swallowed below, and this single login attempt sees "zero users" and gets a spurious 401
        // - "correct password, sometimes fails" is exactly what that looks like from the outside.
        // A plain retry on the next login attempt happens to "fix" it too, which is why this was
        // easy to write off as flaky rather than a real bug.
        string[]? lines = null;
        Exception? lastError = null;
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts && lines is null; attempt++)
        {
            try
            {
                lines = File.ReadAllLines(_csvPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                    Thread.Sleep(150 * attempt);
            }
        }

        if (lines is null)
        {
            _logger.LogError(lastError, "Could not read users CSV at {Path} after {Attempts} attempt(s)", _csvPath, maxAttempts);
            yield break;
        }

        // Skip the header row; ignore blanks and comment lines.
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Split on the FIRST comma only - an email never contains a comma, but a password
            // might, so everything after the first comma is the password.
            var comma = line.IndexOf(',');
            if (comma <= 0)
                continue;

            var email = line[..comma].Trim();
            var password = line[(comma + 1)..];
            yield return (email, password);
        }
    }
}
