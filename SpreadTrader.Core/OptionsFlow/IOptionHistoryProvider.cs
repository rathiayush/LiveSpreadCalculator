namespace SpreadTrader.Core.OptionsFlow;

/// <summary>
/// Fetches historical 1-minute bars for a single option contract, so a series can start at the
/// session open rather than at whenever this process happened to boot.
///
/// This is what makes the ATM shift correct rather than merely smooth. CDV and OBV are cumulative
/// sums over a SET of strikes; when the window re-centres, that set changes, and splicing the new
/// set's numbers onto the old set's running total produces a step that looks exactly like a real
/// flow event but isn't. With history available the engine instead rebuilds the entire day from
/// the session open over the new window, so every point on the curve is a consistent sum over the
/// strikes currently being tracked.
///
/// One minute is the finest granularity available: Dhan's intraday endpoint accepts intervals of
/// 1, 5, 15, 25 or 60 minutes and has nothing below a minute. That is why the whole feature works
/// in minute bars, with only the still-open bar refreshed from live ticks.
/// </summary>
public interface IOptionHistoryProvider
{
    /// <summary>False when the active broker has no usable intraday history for options - Kite's
    /// historical API is a paid add-on, so it reports false and the engine degrades to
    /// accumulating from process start (and says so on the snapshot).</summary>
    bool IsAvailable { get; }

    /// <summary>Minute bars for one contract over a window, oldest first. Returns an empty list
    /// rather than throwing when the broker has nothing for this contract (an illiquid strike that
    /// simply hasn't traded is a normal, expected answer, not a failure).</summary>
    Task<IReadOnlyList<OptionBar>> GetMinuteBarsAsync(
        OptionContract contract,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
