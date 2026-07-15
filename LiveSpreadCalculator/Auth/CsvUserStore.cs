using System.Threading;

namespace LiveSpreadCalculator.Auth;

/// <summary>Validates app-login credentials against a small CSV file (email,password - one user per
/// line, header row required) and lets a signed-in user change their own password. This is the
/// allow-list of who is permitted to obtain a JWT and therefore call the API at all; anyone not in
/// the file cannot log in. Same "a CSV file is a deliberate stopgap, not a real database" spirit as
/// CsvTriggerRuleStore.
///
/// Passwords at rest: the password column is stored ENCRYPTED (see PasswordProtector - "enc:..."
/// blobs), not plaintext. Legacy plaintext rows are still accepted for login and are upgraded to
/// encrypted form by MigrateToEncrypted() on startup. Encryption is reversible on purpose (keyed
/// AES) so the original password can be recovered by anyone holding the key - e.g. the PowerShell
/// script in Tools/Decrypt-Users.ps1. Keep App_Data/users.csv and the key out of source control.
///
/// The file is re-read on every login attempt (cheap - it's a handful of lines) so adding or
/// removing a user takes effect immediately without restarting the app.</summary>
public sealed class CsvUserStore
{
    private readonly string _csvPath;
    private readonly PasswordProtector _protector;
    private readonly ILogger<CsvUserStore> _logger;

    // Serializes the read-modify-write done by ChangePassword / MigrateToEncrypted so two concurrent
    // password changes can't clobber each other's row or corrupt the file mid-write.
    private readonly object _writeLock = new();

    public CsvUserStore(string csvPath, PasswordProtector protector, ILogger<CsvUserStore> logger)
    {
        _csvPath = csvPath;
        _protector = protector;
        _logger = logger;
    }

    /// <summary>Returns true only when the email exists in the CSV and the password matches.
    /// Email comparison is case-insensitive; password comparison is exact (case-sensitive).
    /// Works whether the stored row is an encrypted blob or legacy plaintext.</summary>
    public bool ValidateCredentials(string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
            return false;

        foreach (var (fileEmail, storedPassword) in LoadUsers())
        {
            if (!string.Equals(fileEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;

            var actual = _protector.IsEncrypted(storedPassword) ? _protector.Decrypt(storedPassword) : storedPassword;
            if (string.Equals(actual, password))
                return true;
        }

        return false;
    }

    /// <summary>The current security stamp for a user - a fingerprint of their stored password field
    /// (see PasswordProtector.StampFor). Embedded in the JWT at login and re-checked on every request
    /// in Program.cs; a password change moves the stamp, invalidating every existing token. Returns
    /// an empty string for an unknown email (so any token claiming to be that user fails the check).</summary>
    public string GetSecurityStamp(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return string.Empty;

        foreach (var (fileEmail, storedPassword) in LoadUsers())
        {
            if (string.Equals(fileEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
                return PasswordProtector.StampFor(storedPassword);
        }

        return string.Empty;
    }

    /// <summary>Changes a signed-in user's password after re-verifying their current one. On success
    /// the new password is written back ENCRYPTED, which also changes the user's security stamp and
    /// therefore forces a logout everywhere. Thread-safe and atomic (temp file + replace).</summary>
    public ChangePasswordResult ChangePassword(string? email, string? currentPassword, string? newPassword)
    {
        if (string.IsNullOrWhiteSpace(email))
            return ChangePasswordResult.Fail("Email is required.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            return ChangePasswordResult.Fail("New password must be at least 6 characters.");
        if (string.Equals(currentPassword, newPassword))
            return ChangePasswordResult.Fail("New password must be different from the current one.");
        if (!ValidateCredentials(email, currentPassword))
            return ChangePasswordResult.Fail("Current password is incorrect.");

        lock (_writeLock)
        {
            List<string> lines;
            try
            {
                lines = File.ReadAllLines(_csvPath).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not read users CSV at {Path} while changing password", _csvPath);
                return ChangePasswordResult.Fail("Could not read the user file. Please try again.");
            }

            var updated = false;
            for (var i = 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var comma = line.IndexOf(',');
                if (comma <= 0)
                    continue;

                var rowEmail = line[..comma].Trim();
                if (string.Equals(rowEmail, email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{rowEmail},{_protector.Encrypt(newPassword)}";
                    updated = true;
                    break;
                }
            }

            if (!updated)
                return ChangePasswordResult.Fail("User not found.");

            if (!TryWriteAllLines(lines))
                return ChangePasswordResult.Fail("Could not save the new password. Please try again.");
        }

        return ChangePasswordResult.Success();
    }

    /// <summary>One-time-per-file upgrade: rewrite any legacy plaintext password rows as encrypted
    /// blobs. Called once at startup (see Program.cs). If every row is already encrypted this is a
    /// no-op and does NOT rewrite the file, so stamps (and therefore live sessions) stay stable
    /// across ordinary restarts - only the very first run after this feature ships re-encrypts.</summary>
    public void MigrateToEncrypted()
    {
        lock (_writeLock)
        {
            if (!File.Exists(_csvPath))
            {
                _logger.LogWarning("Users CSV not found at {Path} - nothing to migrate.", _csvPath);
                return;
            }

            List<string> lines;
            try
            {
                lines = File.ReadAllLines(_csvPath).ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not read users CSV at {Path} for encryption migration", _csvPath);
                return;
            }

            var changed = false;
            for (var i = 1; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var comma = line.IndexOf(',');
                if (comma <= 0)
                    continue;

                var rowEmail = line[..comma].Trim();
                var stored = line[(comma + 1)..];
                if (!_protector.IsEncrypted(stored))
                {
                    lines[i] = $"{rowEmail},{_protector.Encrypt(stored)}";
                    changed = true;
                }
            }

            if (!changed)
                return;

            if (TryWriteAllLines(lines))
                _logger.LogInformation("Encrypted {Path}: legacy plaintext passwords upgraded to encrypted storage.", _csvPath);
            else
                _logger.LogError("Failed to write encrypted users CSV at {Path} during migration.", _csvPath);
        }
    }

    /// <summary>Atomic-ish write: write to a temp file then replace the target, with the same short
    /// retry/backoff as the reader below (Windows/AV/indexer can transiently lock the file).</summary>
    private bool TryWriteAllLines(IReadOnlyList<string> lines)
    {
        var tmp = _csvPath + ".tmp";
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.WriteAllLines(tmp, lines);
                // File.Copy(overwrite) rather than File.Move so we don't fail if the target exists.
                File.Copy(tmp, _csvPath, overwrite: true);
                File.Delete(tmp);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (attempt < maxAttempts)
                    Thread.Sleep(150 * attempt);
                else
                    _logger.LogError(ex, "Could not write users CSV at {Path} after {Attempts} attempt(s)", _csvPath, maxAttempts);
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
        // swallowed below, and this single login attempt sees "zero users" and gets a spurious 401.
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
            // (or an encrypted blob) never does either, so everything after the first comma is it.
            var comma = line.IndexOf(',');
            if (comma <= 0)
                continue;

            var email = line[..comma].Trim();
            var password = line[(comma + 1)..];
            yield return (email, password);
        }
    }
}

/// <summary>Outcome of CsvUserStore.ChangePassword - either success, or failure with a user-facing
/// reason the controller can hand straight back to the client.</summary>
public readonly struct ChangePasswordResult
{
    public bool Ok { get; }
    public string? Error { get; }

    private ChangePasswordResult(bool ok, string? error)
    {
        Ok = ok;
        Error = error;
    }

    public static ChangePasswordResult Success() => new(true, null);
    public static ChangePasswordResult Fail(string error) => new(false, error);
}
