using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

/// <summary>
/// File-backed ITriggerRuleStore - keeps rules in memory for fast reads (same as
/// InMemoryTriggerRuleStore) but rewrites a CSV file with the full rule set on every change and
/// loads it back on startup, so trigger configuration survives a restart. This is a deliberate
/// stopgap - the whole file is rewritten on every write, which is fine at the scale of tens or
/// low hundreds of rules but wouldn't scale to a high write rate or many concurrent writers. If
/// that ever matters, swap this for a real database behind the same ITriggerRuleStore interface.
/// </summary>
public sealed class CsvTriggerRuleStore : ITriggerRuleStore
{
    // AutoDirectionFromSpreadSign and ThresholdIsPercent were added after the first version of
    // this file shipped, so they're appended at the end and ParseLine treats them as optional
    // (defaulting to true / false respectively, matching the model defaults) - older CSVs
    // without those columns still load fine.
    private const string Header =
        "Id,SpreadId,Operator,Threshold,NearLegAction,FarLegAction,Quantity,Product,OrderType,OneShot,RearmBuffer,Enabled,IsArmed,LastFiredAt,AutoDirectionFromSpreadSign,ThresholdIsPercent";

    private readonly string _filePath;
    private readonly object _fileLock = new();
    private readonly ConcurrentDictionary<string, TriggerRule> _rules = new();

    public CsvTriggerRuleStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public void Upsert(TriggerRule rule)
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

    public int RemoveAll()
    {
        var count = _rules.Count;
        if (count == 0) return 0;
        _rules.Clear();
        Save();
        return count;
    }

    public IReadOnlyList<TriggerRule> GetAll() => _rules.Values.ToArray();

    public IReadOnlyList<TriggerRule> GetForSpread(string spreadId) =>
        _rules.Values.Where(r => r.SpreadId == spreadId).ToArray();

    public TriggerRule? Get(string ruleId) => _rules.GetValueOrDefault(ruleId);

    private void Load()
    {
        lock (_fileLock)
        {
            // A leftover ".tmp" here means a previous Save() wrote the temp copy successfully
            // but failed to move it into place (e.g. the destination was transiently locked by
            // an antivirus scanner, search indexer, or a sync client) - see Save()/WithRetry.
            // It's never authoritative, so just clear it out rather than let it linger.
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

            foreach (var line in File.ReadAllLines(_filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var rule = ParseLine(line);
                if (rule is not null) _rules[rule.Id] = rule;
            }
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

            // Write to a temp file then move over the real one, so a crash mid-write can't
            // leave a half-written / corrupt CSV behind. Both steps are retried a few times
            // with a short backoff: on Windows the CSV or its temp copy can be transiently
            // locked by an antivirus scanner, a sync client (OneDrive/Dropbox), the search
            // indexer, or an editor that happens to have the file open - none of which are
            // under this process's control, and all of which normally clear within a second
            // or two. Without the retry, that transient lock surfaces as an
            // UnauthorizedAccessException all the way up to the caller (e.g. "add trigger").
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

    private static string FormatLine(TriggerRule r) => string.Join(",",
        Escape(r.Id),
        Escape(r.SpreadId),
        r.Operator,
        r.Threshold.ToString(CultureInfo.InvariantCulture),
        r.NearLegAction,
        r.FarLegAction,
        r.Quantity.ToString(CultureInfo.InvariantCulture),
        Escape(r.Product),
        Escape(r.OrderType),
        r.OneShot,
        r.RearmBuffer.ToString(CultureInfo.InvariantCulture),
        r.Enabled,
        r.IsArmed,
        r.LastFiredAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
        r.AutoDirectionFromSpreadSign,
        r.ThresholdIsPercent);

    private static TriggerRule? ParseLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Length < 14) return null;

        try
        {
            return new TriggerRule
            {
                Id = fields[0],
                SpreadId = fields[1],
                Operator = Enum.Parse<TriggerOperator>(fields[2]),
                Threshold = decimal.Parse(fields[3], CultureInfo.InvariantCulture),
                NearLegAction = Enum.Parse<TriggerLegAction>(fields[4]),
                FarLegAction = Enum.Parse<TriggerLegAction>(fields[5]),
                Quantity = int.Parse(fields[6], CultureInfo.InvariantCulture),
                Product = fields[7],
                OrderType = fields[8],
                OneShot = bool.Parse(fields[9]),
                RearmBuffer = decimal.Parse(fields[10], CultureInfo.InvariantCulture),
                Enabled = bool.Parse(fields[11]),
                IsArmed = bool.Parse(fields[12]),
                LastFiredAt = string.IsNullOrEmpty(fields[13])
                    ? null
                    : DateTimeOffset.Parse(fields[13], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                AutoDirectionFromSpreadSign = fields.Length > 14 && !string.IsNullOrEmpty(fields[14])
                    ? bool.Parse(fields[14])
                    : true,
                ThresholdIsPercent = fields.Length > 15 && !string.IsNullOrEmpty(fields[15])
                    && bool.Parse(fields[15]),
            };
        }
        catch
        {
            return null; // skip malformed rows rather than fail startup over one bad line
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
