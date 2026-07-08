using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using Microsoft.Extensions.Logging;

namespace SpreadTrader.Core.Store;

/// <summary>
/// File-backed IPnlAlertStore - same rewrite-the-whole-CSV-on-every-write, load-on-startup
/// pattern as CsvTriggerRuleStore (see that file's doc comment for the reasoning; this is a much
/// smaller/simpler row shape since a PNL alert never touches order placement, just a Telegram
/// send).
/// </summary>
public sealed class CsvPnlAlertStore : IPnlAlertStore
{
    private const string Header = "Id,SpreadId,Underlying,TargetExitPnl,FireWhenGreaterOrEqual,OneShot,Enabled,LastFiredAt,CreatedAt";

    private readonly string _filePath;
    private readonly object _fileLock = new();
    private readonly ConcurrentDictionary<string, PnlAlertRule> _rules = new();
    private readonly ILogger<CsvPnlAlertStore>? _logger;

    public CsvPnlAlertStore(string filePath, ILogger<CsvPnlAlertStore>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    public void Upsert(PnlAlertRule rule)
    {
        _rules[rule.Id] = rule;
        Save();
    }

    public bool Remove(string ruleId)
    {
        var removed = _rules.TryRemove(ruleId, out _);
        if (removed) Save();
        return removed;
    }

    public IReadOnlyList<PnlAlertRule> GetAll() => _rules.Values.ToArray();

    public PnlAlertRule? Get(string ruleId) => _rules.GetValueOrDefault(ruleId);

    private void Load()
    {
        lock (_fileLock)
        {
            var staleTempPath = _filePath + ".tmp";
            if (File.Exists(staleTempPath))
            {
                try { File.Delete(staleTempPath); } catch { /* best-effort cleanup */ }
            }

            if (!File.Exists(_filePath))
            {
                EnsureDirectory();
                File.WriteAllText(_filePath, Header + Environment.NewLine);
                return;
            }

            var loaded = 0;
            var skipped = 0;
            foreach (var line in File.ReadAllLines(_filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var rule = ParseLine(line);
                if (rule is not null)
                {
                    _rules[rule.Id] = rule;
                    loaded++;
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
                _logger?.LogError(
                    "CsvPnlAlertStore.Load: {Skipped} row(s) in {Path} failed to parse and were dropped - " +
                    "{Loaded} row(s) loaded successfully.", skipped, _filePath, loaded);
        }
    }

    private void Save()
    {
        lock (_fileLock)
        {
            EnsureDirectory();
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var rule in _rules.Values)
                sb.AppendLine(FormatLine(rule));

            var tempPath = _filePath + ".tmp";
            var content = sb.ToString();
            WithRetry(() =>
            {
                ClearReadOnlyIfSet(tempPath);
                File.WriteAllText(tempPath, content);
            });
            WithRetry(() =>
            {
                ClearReadOnlyIfSet(_filePath);
                File.Move(tempPath, _filePath, overwrite: true);
            });
        }
    }

    private static void ClearReadOnlyIfSet(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static void WithRetry(Action action)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(150 * attempt);
            }
        }
    }

    private void EnsureDirectory()
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string FormatLine(PnlAlertRule r) => string.Join(",",
        Escape(r.Id),
        Escape(r.SpreadId),
        Escape(r.Underlying),
        r.TargetExitPnl.ToString(CultureInfo.InvariantCulture),
        r.FireWhenGreaterOrEqual,
        r.OneShot,
        r.Enabled,
        r.LastFiredAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
        r.CreatedAt.ToString("O", CultureInfo.InvariantCulture));

    private PnlAlertRule? ParseLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Length < 9)
        {
            _logger?.LogWarning(
                "CsvPnlAlertStore: dropping a row in {Path} with only {FieldCount} column(s) " +
                "(need at least 9) - raw line: {Line}", _filePath, fields.Length, line);
            return null;
        }

        try
        {
            return new PnlAlertRule
            {
                Id = fields[0],
                SpreadId = fields[1],
                Underlying = fields[2],
                TargetExitPnl = decimal.Parse(fields[3], CultureInfo.InvariantCulture),
                FireWhenGreaterOrEqual = bool.Parse(fields[4]),
                OneShot = bool.Parse(fields[5]),
                Enabled = bool.Parse(fields[6]),
                LastFiredAt = string.IsNullOrEmpty(fields[7])
                    ? null
                    : DateTimeOffset.Parse(fields[7], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                CreatedAt = DateTimeOffset.Parse(fields[8], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            };
        }
        catch (Exception ex)
        {
            var ruleId = fields.Length > 0 ? fields[0] : "(unknown)";
            _logger?.LogWarning(ex,
                "CsvPnlAlertStore: dropping unparseable row for alert id {RuleId} in {Path} - {Reason}. " +
                "Raw line: {Line}", ruleId, _filePath, ex.Message, line);
            return null;
        }
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
                current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
