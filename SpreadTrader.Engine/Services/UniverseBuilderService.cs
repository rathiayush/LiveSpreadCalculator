using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.Engine.Services;

/// <summary>
/// On demand (via POST /api/universe/build), turns every stock Kite currently lists futures
/// for on NFO into a live-streamed calendar spread universe in one call - no hand-maintained
/// symbol list required:
///   1. asks the Kite instrument master for every distinct underlying with an NFO futures
///      contract right now (see IBrokerInstrumentMaster.GetAllFuturesUnderlyings), optionally
///      capped to the first maxSymbols of them,
///   2. resolves each symbol's near/far month futures contract,
///   3. writes the resolved set to a CSV (App_Data/universe_futures.csv) - this is the
///      "master list" of what's actually tradeable right now,
///   4. upserts a SpreadDefinition for every resolved row and subscribes the live ticker to it,
///      so all of them start streaming immediately without configuring each one by hand.
/// Symbols that only have one futures contract listed right now (near month about to expire
/// with no far month yet rolled) are skipped rather than failing the whole batch - see
/// UniverseBuildResult.Skipped. Re-run this after every monthly expiry roll, or whenever you
/// want to pick up newly-added F&O stocks - nothing here re-runs automatically.
/// </summary>
public sealed class UniverseBuilderService
{
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly ISpreadDefinitionStore _spreadStore;
    private readonly TickIngestionHostedService _tickIngestion;
    private readonly ILogger<UniverseBuilderService> _logger;
    private readonly string _outputCsvPath;
    private readonly McxOptions _mcx;
    private readonly string _mcxOutputCsvPath;

    // Serializes NFO and MCX builds against each other so two overlapping build calls (e.g. an
    // admin double-clicking "Rebuild") can never race on the "remove my own previous spread(s),
    // then insert the new one(s)" sequences below - see _lastNfoBuiltSpreadIds /
    // _lastMcxCrossContractSpreadIds / _mcxCalendarSpreadIdBySymbol.
    private readonly SemaphoreSlim _buildLock = new(1, 1);

    // The spread IDs THIS SERVICE inserted on its most recent NFO build, so the next build can
    // remove exactly those (and only those) before inserting the new set - a true "replace", not an
    // accumulate (e.g. if the front month rolls between builds, the old now-stale spread is removed
    // rather than left orphaned alongside the new one). Deliberately never touches spreads added any
    // other way (manual AddSpreadForm entries) since those IDs are never added to this set.
    // In-memory only, consistent with ISpreadDefinitionStore itself being in-memory (see
    // Program.cs's "State (Core)" comment) - a restart already loses spread definitions entirely.
    private HashSet<string> _lastNfoBuiltSpreadIds = new(StringComparer.OrdinalIgnoreCase);

    // Cross-contract spread IDs only (SILVER-SILVERM etc.) from the MCX build - see BuildMcxAsync.
    // Same "true replace" reasoning as _lastNfoBuiltSpreadIds above.
    private HashSet<string> _lastMcxCrossContractSpreadIds = new(StringComparer.OrdinalIgnoreCase);

    // The currently-live calendar spreadId for each MCX commodity symbol, so a rebuild can remove
    // the OLD one before inserting the new one if the symbol's near/far contracts changed since the
    // last build (e.g. an expiry rolled) - same "true replace" idea as the two sets above, just
    // keyed per-symbol since each commodity's calendar spread is upserted independently within one
    // MCX build - see UpsertMcxCalendarSpread.
    private readonly Dictionary<string, string> _mcxCalendarSpreadIdBySymbol = new(StringComparer.OrdinalIgnoreCase);

    public UniverseBuilderService(
        IBrokerInstrumentMaster instrumentMaster,
        ISpreadDefinitionStore spreadStore,
        TickIngestionHostedService tickIngestion,
        ILogger<UniverseBuilderService> logger,
        string outputCsvPath,
        McxOptions mcx,
        string mcxOutputCsvPath)
    {
        _instrumentMaster = instrumentMaster;
        _spreadStore = spreadStore;
        _tickIngestion = tickIngestion;
        _logger = logger;
        _outputCsvPath = outputCsvPath;
        _mcx = mcx;
        _mcxOutputCsvPath = mcxOutputCsvPath;
    }

    /// <param name="maxSymbols">Optional cap on how many symbols (alphabetically, from Kite's
    /// full NFO futures list) to build. Null/omitted = every stock currently listed for futures
    /// on NFO - roughly 200-220 as of mid-2026, since that's the entire exchange's F&O universe,
    /// not an artificial limit in this code.</param>
    public async Task<UniverseBuildResult> BuildAsync(int? maxSymbols, CancellationToken cancellationToken)
    {
        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            await _instrumentMaster.EnsureLoadedAsync(cancellationToken);

            var symbols = _instrumentMaster.GetAllFuturesUnderlyings("NFO");
            if (maxSymbols is > 0 && maxSymbols < symbols.Count)
                symbols = symbols.Take(maxSymbols.Value).ToArray();

            var included = new List<UniverseBuildRow>();
            var skipped = new List<string>();
            // Collected across every spread and subscribed in one batch at the end - see
            // TickIngestionHostedService.SubscribeTokensAsync for why per-spread subscribing is
            // unreliable at this scale.
            var allTokens = new HashSet<uint>();
            var builtSpreadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var symbol in symbols)
            {
                var contracts = _instrumentMaster.FindFutures(symbol, "NFO");
                if (contracts.Count < 2)
                {
                    skipped.Add($"{symbol} (only {contracts.Count} futures contract(s) currently listed - needs a near and a far month)");
                    continue;
                }

                var near = contracts[0];
                var far = contracts[1];

                var spreadId = $"{symbol}-{MonthAbbrev(near.Expiry)}-{MonthAbbrev(far.Expiry)}";
                var spread = new SpreadDefinition
                {
                    Id = spreadId,
                    Underlying = symbol,
                    NearLeg = new InstrumentConfig
                    {
                        TradingSymbol = near.TradingSymbol,
                        Exchange = near.Exchange,
                        InstrumentToken = near.InstrumentToken,
                        Underlying = symbol,
                        Expiry = near.Expiry,
                        LotSize = near.LotSize,
                        TickSize = near.TickSize,
                    },
                    FarLeg = new InstrumentConfig
                    {
                        TradingSymbol = far.TradingSymbol,
                        Exchange = far.Exchange,
                        InstrumentToken = far.InstrumentToken,
                        Underlying = symbol,
                        Expiry = far.Expiry,
                        LotSize = far.LotSize,
                        TickSize = far.TickSize,
                    },
                    Enabled = true,
                };

                _spreadStore.Upsert(spread);
                builtSpreadIds.Add(spreadId);
                allTokens.Add(near.InstrumentToken);
                allTokens.Add(far.InstrumentToken);

                included.Add(new UniverseBuildRow(
                    symbol, spreadId,
                    near.TradingSymbol, near.Expiry, near.LotSize,
                    far.TradingSymbol, far.Expiry, far.LotSize));
            }

            // True "replace": drop whatever THIS build type inserted last time but isn't part of
            // this run - e.g. if a symbol's near/far contracts rolled to a new expiry since the last
            // build, the old now-stale spread is removed instead of left orphaned alongside the new
            // one. Computed as a set difference so anything that legitimately reappears unchanged is
            // upserted in place above rather than removed-then-recreated. Manually-added spreads
            // (never in _lastNfoBuiltSpreadIds) are never touched.
            var stale = _lastNfoBuiltSpreadIds.Except(builtSpreadIds, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var staleId in stale)
                _spreadStore.Remove(staleId);
            _lastNfoBuiltSpreadIds = builtSpreadIds;

            try
            {
                await _tickIngestion.SubscribeTokensAsync(allTokens, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe ticker for {Count} universe tokens", allTokens.Count);
            }

            await WriteOutputCsvAsync(included, cancellationToken);

            _logger.LogInformation(
                "Universe build: {Included}/{Requested} symbols resolved, {Removed} stale spread(s) from the previous build removed, {Skipped} skipped, {Tokens} instrument tokens subscribed in one batch",
                included.Count, symbols.Count, stale.Length, skipped.Count, allTokens.Count);

            return new UniverseBuildResult(symbols.Count, included, skipped);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>
    /// The MCX commodity counterpart to BuildAsync. Only reachable when Mcx:Enabled is true (the
    /// endpoint guards it - see UniverseController) so it never touches the NFO path. Builds two
    /// kinds of commodity arbitrage spreads from the live MCX instrument master, both configurable
    /// on/off in McxOptions:
    ///
    ///  1. CALENDAR spreads (McxOptions.CalendarSpreads) - for each configured underlying
    ///     (SILVER, GOLD, CRUDEOIL, ...), its nearest-expiry vs next-expiry future. The spread
    ///     value the engine computes (FarLeg - NearLeg) is the calendar carry; the edge appears
    ///     when that carry deviates from fair cost-of-carry (interest + storage + insurance) - the
    ///     classic silver calendar arbitrage.
    ///
    ///  2. CROSS-CONTRACT spreads (McxOptions.CrossContractSpreads) - for each group of same-
    ///     commodity specs (e.g. SILVER / SILVERM / SILVERMIC), every distinct pair, matched on
    ///     the SAME expiry so the difference is pure per-kg spec mispricing and NOT contaminated
    ///     by calendar carry. All silver variants are quoted in Rs/kg, so their prices are
    ///     directly comparable and should be near-identical; the deviation is the arbitrage.
    ///
    /// IMPORTANT caveat carried into every cross-contract row's log: the legs have different lot
    /// sizes (SILVER 30kg, SILVERM 5kg, SILVERMIC 1kg), so a naive 1-lot-vs-1-lot basket is NOT
    /// kg-neutral. Balancing quantities to equal kg exposure is the responsibility of the trigger
    /// rule's per-leg quantity, not this pairing.
    /// </summary>
    public async Task<UniverseBuildResult> BuildMcxAsync(CancellationToken cancellationToken)
    {
        if (!_mcx.Enabled)
            throw new InvalidOperationException("MCX add-on is disabled (set Mcx:Enabled=true to use it).");

        await _buildLock.WaitAsync(cancellationToken);
        try
        {
        await _instrumentMaster.EnsureLoadedAsync(cancellationToken);

        var included = new List<UniverseBuildRow>();
        var skipped = new List<string>();
        var allTokens = new HashSet<uint>();
        var seenSpreadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requested = 0;

        // Shared upsert+subscribe so calendar and cross-contract rows go through identical wiring.
        void AddSpread(string spreadId, string underlying, BrokerInstrument near, BrokerInstrument far)
        {
            if (!seenSpreadIds.Add(spreadId))
                return; // already built this exact pair in this run

            _spreadStore.Upsert(new SpreadDefinition
            {
                Id = spreadId,
                Underlying = underlying,
                NearLeg = ToLeg(near, underlying),
                FarLeg = ToLeg(far, underlying),
                Enabled = true,
            });
            allTokens.Add(near.InstrumentToken);
            allTokens.Add(far.InstrumentToken);
            included.Add(new UniverseBuildRow(
                underlying, spreadId,
                near.TradingSymbol, near.Expiry, near.LotSize,
                far.TradingSymbol, far.Expiry, far.LotSize));
        }

        // 1. Calendar spreads (near vs far expiry of the same commodity) - always the two
        // nearest-listed expiries per commodity.
        if (_mcx.CalendarSpreads)
        {
            foreach (var symbol in _mcx.Underlyings.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                requested++;
                var contracts = _instrumentMaster.FindFutures(symbol, _mcx.Exchange);
                if (contracts.Count < 2)
                {
                    skipped.Add($"{symbol} calendar (only {contracts.Count} MCX contract(s) currently listed - needs a near and a far expiry)");
                    continue;
                }

                var near = contracts[0];
                var far = contracts[1];
                included.Add(UpsertMcxCalendarSpread(symbol, near, far, allTokens));
            }
        }

        // 2. Cross-contract spreads (SILVER vs SILVERM vs SILVERMIC etc.), closest-expiry matched
        //    (the big and mini/micro contracts often list different expiry cycles).
        if (_mcx.CrossContractSpreads)
        {
            foreach (var group in _mcx.CrossContractGroups)
            {
                var legs = group.Legs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (var i = 0; i < legs.Count; i++)
                {
                    for (var j = i + 1; j < legs.Count; j++)
                    {
                        requested++;
                        var a = legs[i];
                        var b = legs[j];
                        var contractsA = _instrumentMaster.FindFutures(a, _mcx.Exchange);
                        var contractsB = _instrumentMaster.FindFutures(b, _mcx.Exchange);
                        if (contractsA.Count == 0 || contractsB.Count == 0)
                        {
                            skipped.Add($"{a}/{b} cross-contract (one side not listed on MCX right now)");
                            continue;
                        }

                        // Match on the CLOSEST-expiry pair (gap 0 = a true same-expiry match).
                        // The big contract and its mini/micro siblings often list different expiry
                        // cycles (e.g. SILVER Sep/Dec vs SILVERM Aug/Nov), so insisting on an
                        // identical expiry would skip the SILVER-vs-mini pairs entirely - see
                        // McxOptions.MaxCrossContractExpiryGapDays.
                        var match = FindClosestExpiryPair(contractsA, contractsB);
                        if (match is not { } m)
                        {
                            skipped.Add($"{a}/{b} cross-contract (neither side has a listed expiry right now)");
                            continue;
                        }

                        if (m.GapDays > _mcx.MaxCrossContractExpiryGapDays)
                        {
                            skipped.Add($"{a}/{b} cross-contract (closest expiries {MonthAbbrev(m.EarlierExpiry.Expiry)}/{MonthAbbrev(m.LaterExpiry.Expiry)} are {m.GapDays}d apart, over the {_mcx.MaxCrossContractExpiryGapDays}d cap - would be mostly calendar carry, not spec mispricing)");
                            continue;
                        }

                        // NearLeg = earlier-expiry contract, FarLeg = later-expiry, so the engine's
                        // FarLeg-NearLeg reads as the per-kg gap between the two specs (plus, when
                        // GapDays > 0, the small carry between their nearest expiries). SpreadId
                        // uses day-precise ExpiryTag (not just month) for the same reason as the
                        // calendar-spread branch above: two contracts can share a month but expire
                        // on different exact days, and a month-only ID would risk two genuinely
                        // different pairs colliding on the same spreadId.
                        var near = m.EarlierExpiry;
                        var far = m.LaterExpiry;
                        var spreadId = m.GapDays == 0
                            ? $"{near.Name}-{far.Name}-{ExpiryTag(near.Expiry)}"
                            : $"{near.Name}{ExpiryTag(near.Expiry)}-{far.Name}{ExpiryTag(far.Expiry)}";

                        // Underlying label names BOTH specs in the pair (group order, e.g.
                        // "SILVER-SILVERM"), not just the base symbol - a cross-contract spread's
                        // "underlying" genuinely is the pair, and this is what the dashboard groups/
                        // displays spreads by, so a single-symbol label would be misleading here.
                        var underlying = $"{a}-{b}";
                        AddSpread(spreadId, underlying, near, far);
                        _logger.LogInformation(
                            "MCX cross-contract spread {SpreadId}: {NearSym} ({NearLot}kg-basis lot, {NearExp}) vs {FarSym} ({FarLot}kg-basis lot, {FarExp}), expiry gap {Gap}d - NOTE lot sizes differ, balance trigger quantities for kg-neutral exposure{CarryNote}",
                            spreadId, near.TradingSymbol, near.LotSize, MonthAbbrev(near.Expiry),
                            far.TradingSymbol, far.LotSize, MonthAbbrev(far.Expiry), m.GapDays,
                            m.GapDays > 0 ? "; spread includes ~1 expiry of calendar carry between the two specs" : "");
                    }
                }
            }
        }

        // True "replace" for CROSS-CONTRACT spreads only (calendar spreads are already replaced
        // in place, per symbol, by UpsertMcxCalendarSpread above - see
        // _mcxCalendarSpreadIdBySymbol's doc comment for why calendar needed its own tracking).
        // seenSpreadIds only ever receives cross-contract IDs now (AddSpread is no longer called
        // by the calendar loop), so this drops whatever cross-contract pairs the last bulk build
        // inserted that this run didn't recreate. Manually-added spreads are untouched.
        var staleCrossContract = _lastMcxCrossContractSpreadIds.Except(seenSpreadIds, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var staleId in staleCrossContract)
            _spreadStore.Remove(staleId);
        _lastMcxCrossContractSpreadIds = seenSpreadIds;
        var stale = staleCrossContract;

        try
        {
            await _tickIngestion.SubscribeTokensAsync(allTokens, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe ticker for {Count} MCX universe tokens", allTokens.Count);
        }

        await WriteOutputCsvToAsync(_mcxOutputCsvPath, included, cancellationToken);

        _logger.LogInformation(
            "MCX universe build: {Included} commodity spreads built, {Removed} stale spread(s) from the previous build removed ({Skipped} skipped), {Tokens} instrument tokens subscribed in one batch",
            included.Count, stale.Length, skipped.Count, allTokens.Count);

        return new UniverseBuildResult(requested, included, skipped);
        }
        finally
        {
            _buildLock.Release();
        }
    }

    /// <summary>Upserts one commodity's calendar spread (near/far picked by the caller), and -
    /// critically - looks up whatever calendar spreadId was PREVIOUSLY live for this symbol and
    /// removes it first if this build's near/far produced a different spreadId (e.g. the front
    /// month rolled between builds). This is what keeps a rebuild from leaving a stale duplicate
    /// calendar spread behind for a symbol whose contracts changed since the last build.</summary>
    private UniverseBuildRow UpsertMcxCalendarSpread(string symbol, BrokerInstrument near, BrokerInstrument far, HashSet<uint> allTokens)
    {
        // Day-precise tag (not just month) - MCX contracts of the same commodity can expire on
        // different days even within what looks like "the same month" at a glance (e.g. one
        // contract 4-Sep, another 30-Sep). The exact day always comes from Kite's own instrument-
        // master expiry date, never parsed from the tradingsymbol's year+month text - see
        // ExpiryTag's doc comment.
        var spreadId = $"{symbol}-{ExpiryTag(near.Expiry)}-{ExpiryTag(far.Expiry)}";

        if (_mcxCalendarSpreadIdBySymbol.TryGetValue(symbol, out var previousId)
            && !string.Equals(previousId, spreadId, StringComparison.OrdinalIgnoreCase))
        {
            _spreadStore.Remove(previousId);
        }

        _spreadStore.Upsert(new SpreadDefinition
        {
            Id = spreadId,
            Underlying = symbol,
            NearLeg = ToLeg(near, symbol),
            FarLeg = ToLeg(far, symbol),
            Enabled = true,
        });
        _mcxCalendarSpreadIdBySymbol[symbol] = spreadId;
        allTokens.Add(near.InstrumentToken);
        allTokens.Add(far.InstrumentToken);

        return new UniverseBuildRow(
            symbol, spreadId,
            near.TradingSymbol, near.Expiry, near.LotSize,
            far.TradingSymbol, far.Expiry, far.LotSize);
    }

    private static InstrumentConfig ToLeg(BrokerInstrument instrument, string underlying) => new()
    {
        TradingSymbol = instrument.TradingSymbol,
        Exchange = instrument.Exchange,
        InstrumentToken = instrument.InstrumentToken,
        Underlying = underlying,
        Expiry = instrument.Expiry,
        LotSize = instrument.LotSize,
        TickSize = instrument.TickSize,
    };

    /// <summary>One matched cross-contract pair: the two contracts whose expiries are closest,
    /// already ordered so EarlierExpiry &lt;= LaterExpiry, plus the gap between them in days
    /// (0 = a genuine same-expiry match). Names/tokens live on the KiteInstruments themselves.</summary>
    private readonly record struct CrossPair(BrokerInstrument EarlierExpiry, BrokerInstrument LaterExpiry, int GapDays);

    /// <summary>Across every (a-contract, b-contract) combination that both have a listed expiry,
    /// returns the pair whose expiries are CLOSEST together, tie-broken toward the nearest
    /// (front-month) expiries so the most liquid contracts win. Returns null only if one side has
    /// no dated contract at all. The caller enforces the max-gap cap - see BuildMcxAsync /
    /// McxOptions.MaxCrossContractExpiryGapDays. This is what lets SILVER (Sep/Dec) still pair with
    /// SILVERM (Aug/Nov) at their nearest expiries instead of being skipped for lack of an exact
    /// same-month match.</summary>
    private static CrossPair? FindClosestExpiryPair(
        IReadOnlyList<BrokerInstrument> contractsA,
        IReadOnlyList<BrokerInstrument> contractsB)
    {
        CrossPair? best = null;
        foreach (var a in contractsA.Where(c => c.Expiry is not null))
        {
            foreach (var b in contractsB.Where(c => c.Expiry is not null))
            {
                var earlier = a.Expiry <= b.Expiry ? a : b;
                var later = a.Expiry <= b.Expiry ? b : a;
                var gap = Math.Abs(later.Expiry!.Value.DayNumber - earlier.Expiry!.Value.DayNumber);
                var candidate = new CrossPair(earlier, later, gap);

                if (best is not { } current
                    || candidate.GapDays < current.GapDays
                    || (candidate.GapDays == current.GapDays && candidate.EarlierExpiry.Expiry < current.EarlierExpiry.Expiry))
                {
                    best = candidate;
                }
            }
        }
        return best;
    }

    private Task WriteOutputCsvAsync(IReadOnlyList<UniverseBuildRow> rows, CancellationToken cancellationToken) =>
        WriteOutputCsvToAsync(_outputCsvPath, rows, cancellationToken);

    private static async Task WriteOutputCsvToAsync(string path, IReadOnlyList<UniverseBuildRow> rows, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine("Underlying,SpreadId,NearTradingSymbol,NearExpiry,NearLotSize,FarTradingSymbol,FarExpiry,FarLotSize");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",",
                r.Underlying, r.SpreadId,
                r.NearTradingSymbol, r.NearExpiry?.ToString("yyyy-MM-dd") ?? "", r.NearLotSize,
                r.FarTradingSymbol, r.FarExpiry?.ToString("yyyy-MM-dd") ?? "", r.FarLotSize));
        }

        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, sb.ToString(), cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string MonthAbbrev(DateOnly? expiry) =>
        expiry is { } d ? d.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant() : "XXX";

    /// <summary>Day-precise expiry tag for MCX spread IDs, e.g. "31AUG" or "04SEP" - deliberately
    /// NOT just the month (see MonthAbbrev). MCX commodity contracts (and their mini/micro
    /// siblings) can expire on genuinely different days within what reads as "the same month" -
    /// the exact day always comes from Kite's own instrument-master expiry date (never guessed or
    /// parsed from the tradingsymbol's year+month text, which only ever encodes year+month - the
    /// "26" in a tradingsymbol like SILVERMIC26AUGFUT is the 2-digit YEAR 2026, not a day). Using
    /// only the month here would risk two distinct near/far pairs landing on the same spreadId and
    /// silently overwriting each other in the spread store.</summary>
    private static string ExpiryTag(DateOnly? expiry) =>
        expiry is { } d ? d.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant() : "XXX";
}
