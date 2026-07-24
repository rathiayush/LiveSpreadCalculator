using Microsoft.AspNetCore.Mvc;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

[ApiController]
[Route("api/instruments")]
public sealed class InstrumentsController : ControllerBase
{
    private readonly IBrokerInstrumentMaster _instrumentMaster;

    public InstrumentsController(IBrokerInstrumentMaster instrumentMaster)
    {
        _instrumentMaster = instrumentMaster;
    }

    /// <summary>Lists the futures contracts currently listed for an underlying, ordered by
    /// expiry ascending, so the UI can offer "near month" / "far month" without the user
    /// having to know exact tradingsymbols like "RELIANCE26JULFUT" up front.</summary>
    [HttpGet("futures")]
    public async Task<ActionResult<object>> GetFutures([FromQuery] string underlying, [FromQuery] string exchange, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest("underlying is required");

        await _instrumentMaster.EnsureLoadedAsync(cancellationToken);
        var futures = _instrumentMaster.FindFutures(underlying, string.IsNullOrWhiteSpace(exchange) ? "NFO" : exchange);
        return Ok(futures);
    }

    /// <summary>Downloads Kite's full instrument master (if not already cached) and writes every
    /// futures contract - options and equities excluded - to the futures CSV file configured
    /// under Storage:FuturesCsvPath. Call this once (e.g. on first setup, or whenever you want
    /// a fresh symbol list) rather than on every page load.</summary>
    [HttpPost("futures/fetch-all")]
    public async Task<ActionResult<object>> FetchAllFutures([FromQuery] string? exchange, CancellationToken cancellationToken)
    {
        var futures = await _instrumentMaster.FetchAndSaveFuturesAsync(exchange, cancellationToken);
        return Ok(new { count = futures.Count, futures });
    }

    /// <summary>Lists every cached futures contract (optionally filtered by exchange) for the
    /// trigger-config page's symbol picker. Fast - reads from the in-memory cache populated by
    /// fetch-all (or lazily loads it if nothing has been fetched yet).</summary>
    [HttpGet("futures/all")]
    public async Task<ActionResult<object>> GetAllFutures([FromQuery] string? exchange, CancellationToken cancellationToken)
    {
        await _instrumentMaster.EnsureLoadedAsync(cancellationToken);
        return Ok(_instrumentMaster.GetAllFutures(exchange));
    }
}
