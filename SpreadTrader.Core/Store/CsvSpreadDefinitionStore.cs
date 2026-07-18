using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;

namespace SpreadTrader.Core.Store;

/// <summary>
/// File-backed ISpreadDefinitionStore - same read-everything-into-memory/rewrite-the-whole-file-
/// on-every-change shape as CsvTriggerRuleStore, so spread definitions (which symbols form a
/// calendar spread) survive a restart the same way trigger rules already do. Before this existed,
/// ISpreadDefinitionStore was pure in-memory (InMemorySpreadDefinitionStore) and every configured
/// spread was silently gone after any restart/redeploy - positions opened against those spreads
/// would still show up on Positions & PNL (they come straight from Kite), but with no matching
/// SpreadDefinition left to group them under, so the whole "group near/far legs together, show
/// Square off spread / Add trigger / PNL Alert" UI on PositionsTable.jsx would fall back to plain
/// ungrouped single-leg rows - exactly as if the spread had never been configured at all.
///
/// Only the IDENTIFYING fields are persisted (Id, Underlying, each leg's TradingSymbol/Exchange,
/// Enabled) - never InstrumentToken/LotSize/TickSize/leg Expiry, which are always re-resolved
/// fresh from the live Kite instrument master on every startup regardless of where a
/// SpreadDefinition came from (see TickIngestionHostedService.ExecuteAsync's startup loop, which
/// already does this unconditionally for every spread in the store - reloading from CSV needs no
/// changes there). Persisting a possibly-months-stale resolved token/lot size from disk instead
/// would risk silently using wrong data after Kite's own instrument master changes (e.g. a new
/// expiry cycle) - always re-resolving live avoids that entirely.
/// </summary>
public sealed class CsvSpreadDefinitionStore : ISpreadDefinitionStore
{
    private const string Header = "Id,Underlying,NearLegTradingSymbol,NearLegExchange,FarLegTradingSymbol,FarLegExchange,Enabled";

    private readonly string _filePath;
    private readonly object _fileLock = new();
    private readonly ConcurrentDictionary<string, SpreadDefinition> _definitions = new();
    private readonly ILogger<CsvSpreadDefinitionStore>? _logger;

    public CsvSpreadDefinitionStore(string filePath, ILogger<CsvSpreadDefinitionStore>? logger = null)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    public void Upsert(SpreadDefinition definition)
    {
        _definitions[definition.Id] = definition;
        Save();
    }

    public bool Remove(string spreadId)
    {
        var removed = _definitions.TryRemove(spreadId, out _);
        if (removed) Save();
        return removed;
    }

    public IReadOnlyList<SpreadDefinition> GetAll() => _definitions.Values.ToArray();

    public SpreadDefinition? Get(string spreadId) => _definitions.GetValueOrDefault(spreadId);

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
                var definition = ParseLine(line);
                if (definition is not null)
                {
                    _definitions[definition.Id] = definition;
                    loaded++;
                }
                else
                {
                    skipped++;
                }
            }

            if (skipped > 0)
                _logger?.LogError(
                    "CsvSpreadDefinitionStore.Load: {Skipped} row(s) in {Path} failed to parse and were dropped. " +
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
            foreach (var d in _definitions.Values)
                sb.AppendLine(FormatLine(d));

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

    private static string FormatLine(SpreadDefinition d) => string.Join(",",
        Escape(d.Id),
        Escape(d.Underlying),
        Escape(d.NearLeg.TradingSymbol),
        Escape(d.NearLeg.Exchange),
        Escape(d.FarLeg.TradingSymbol),
        Escape(d.FarLeg.Exchange),
        d.Enabled);

    private SpreadDefinition? ParseLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Length < 7)
        {
            _logger?.LogWarning(
                "CsvSpreadDefinitionStore: dropping a row in {Path} with only {FieldCount} column(s) " +
                "(need at least 7) - raw line: {Line}", _filePath, fields.Length, line);
            return null;
        }

        try
        {
            return new SpreadDefinition
            {
                Id = fields[0],
                Underlying = fields[1],
                // InstrumentToken/LotSize/TickSize/Expiry are deliberately left at their defaults
                // (0/null) here - see this class's doc comment for why they're always re-resolved
                // live from the instrument master at startup rather than persisted.
                NearLeg = new InstrumentConfig { TradingSymbol = fields[2], Exchange = fields[3] },
                FarLeg = new InstrumentConfig { TradingSymbol = fields[4], Exchange = fields[5] },
                Enabled = bool.Parse(fields[6]),
            };
        }
        catch (Exception ex)
        {
            var spreadId = fields.Length > 0 ? fields[0] : "(unknown)";
            _logger?.LogWarning(ex,
                "CsvSpreadDefinitionStore: dropping unparseable row for spread id {SpreadId} in {Path} - " +
                "{Reason}. Raw line: {Line}", spreadId, _filePath, ex.Message, line);
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
