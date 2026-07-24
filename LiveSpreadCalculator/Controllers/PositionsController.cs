using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;
using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Controllers;

/// <summary>
/// Live open positions + PNL, and manual square-off - lets the dashboard cover what you'd
/// otherwise open the Kite app/web for, without triggering the master-logout that invalidates
/// this app's own Kite session (see KiteAuthService's doc comment).
/// </summary>
[ApiController]
[Route("api/positions")]
public sealed class PositionsController : ControllerBase
{
    private readonly IPositionsService _positionsService;
    private readonly IOrderGateway _orderGateway;
    private readonly MarketProtectionService _marketProtection;
    private readonly IBrokerInstrumentMaster _instrumentMaster;
    private readonly IOrderExecutionSink _executionSink;
    private readonly ILogger<PositionsController> _logger;

    public PositionsController(
        IPositionsService positionsService,
        IOrderGateway orderGateway,
        MarketProtectionService marketProtection,
        IBrokerInstrumentMaster instrumentMaster,
        IOrderExecutionSink executionSink,
        ILogger<PositionsController> logger)
    {
        _positionsService = positionsService;
        _orderGateway = orderGateway;
        _marketProtection = marketProtection;
        _instrumentMaster = instrumentMaster;
        _executionSink = executionSink;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PositionsResult>> GetPositions(CancellationToken cancellationToken)
    {
        var result = await _positionsService.GetPositionsAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>Places an opposite-side order to exit some or all of an existing open position.
    /// Re-fetches positions first rather than trusting a client-supplied side/quantity blindly -
    /// both so the correct BUY/SELL direction is derived from the position's actual current sign,
    /// and so a stale UI (position already partly closed elsewhere, or by the engine's own auto
    /// square-off) can't over-exit it.</summary>
    [HttpPost("square-off")]
    public async Task<ActionResult<OrderResult>> SquareOff([FromBody] SquareOffPositionRequest request, CancellationToken cancellationToken)
    {
        if (request.Lots <= 0)
            return BadRequest("Lots must be greater than zero.");

        if (request.SlippagePercent <= 0)
            return BadRequest("SlippagePercent must be greater than zero.");

        var positionsResult = await _positionsService.GetPositionsAsync(cancellationToken);
        if (!positionsResult.Success)
        {
            return Problem(
                title: "Could not fetch positions to verify square-off",
                detail: positionsResult.ErrorMessage,
                statusCode: StatusCodes.Status502BadGateway);
        }

        var position = positionsResult.Positions.FirstOrDefault(p =>
            string.Equals(p.TradingSymbol, request.TradingSymbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Exchange, request.Exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Product, request.Product, StringComparison.OrdinalIgnoreCase));

        if (position is null)
            return NotFound($"No open {request.Product} position in {request.TradingSymbol} on {request.Exchange}.");

        if (position.LotSize <= 0)
        {
            return BadRequest(
                "Lot size not resolved for this instrument yet - fetch/build the instrument " +
                "master first (Symbols page), then retry.");
        }

        var requestedQuantity = request.Lots * position.LotSize;
        var openQuantity = Math.Abs(position.Quantity);
        if (requestedQuantity > openQuantity)
        {
            var openLots = openQuantity / position.LotSize;
            return BadRequest($"Cannot exit {request.Lots} lot(s) ({requestedQuantity}) - only {openLots} lot(s) ({openQuantity}) open on this position.");
        }

        // The instrument-master lookup supplies tick size for market protection below. The TOKEN,
        // though, is taken from the position itself whenever it has one: the positions service
        // resolved it from the most reliable source that broker offers (Dhan puts securityId on
        // the position row), whereas this lookup silently yields 0 if the broker spells the symbol
        // differently from the instrument master - and a 0 token means a broker that addresses
        // orders by id cannot place this square-off at all.
        var instrument = _instrumentMaster.Find(position.TradingSymbol, position.Exchange);
        var instrumentToken = position.InstrumentToken != 0
            ? position.InstrumentToken
            : instrument?.InstrumentToken ?? 0;

        var leg = new OrderLegRequest
        {
            TradingSymbol = position.TradingSymbol,
            Exchange = position.Exchange,
            InstrumentToken = instrumentToken,
            // Opposite of however the position is currently held: SELL cuts a long, BUY covers
            // a short. Derived from the position's own sign rather than anything the client sent.
            TransactionType = position.Quantity > 0 ? "SELL" : "BUY",
            Quantity = requestedQuantity,
            Product = position.Product,
            OrderType = "MARKET",
            Validity = "DAY",
            Tag = "manual-squareoff",
        };

        // Kite rejects a plain MARKET order for F&O outright ("Market orders without market
        // protection are not allowed via API") - convert to a protected IOC LIMIT order first,
        // same as every other MARKET-originated leg in this app (triggered baskets and their
        // auto-square-off go through the identical MarketProtectionService.Protect). Routed
        // through the same tick-store-backed Protect() every other order leg uses, priced off
        // live best bid/ask - never off position.LastPrice (the broker's REST LTP, which can
        // be stale between prints). If this instrument isn't part of any currently-subscribed
        // spread, ITickStore has no depth for it and Protect() sends the leg unprotected rather
        // than falling back to LTP - the broker will then reject the bare MARKET order outright
        // instead of risking a fill priced off a stale reference.
        var protectedLeg = _marketProtection.Protect(
            leg,
            // Same token as the leg above - market protection uses it to read this instrument's
            // live depth out of the tick store, so it must be the one the position is subscribed
            // under (see PositionsCache), not a possibly-unresolved symbol lookup.
            instrumentToken,
            instrument?.TickSize ?? 0,
            $"Manual square-off {position.TradingSymbol}",
            // No TriggerRule to draw a per-rule slippage from here - this is a manual, ad-hoc
            // exit, not a fired rule - so it's whatever the user entered on the square-off
            // confirmation popup (see SquareOffPositionRequest.SlippagePercent), not the
            // EngineOptions app-wide default anymore.
            request.SlippagePercent);

        var result = await _orderGateway.PlaceOrderAsync(protectedLeg, cancellationToken);

        // Reuses the same BasketExecutionEvent/IOrderExecutionSink pipeline triggered baskets
        // use, so a manual square-off shows up in the Trigger Activity feed and fires the same
        // Telegram notification (see TelegramOrderExecutionSink) instead of only being visible
        // if you happen to be looking at the Positions page when it happens.
        var legResult = new OrderLegResult(
            protectedLeg.TradingSymbol,
            protectedLeg.TransactionType,
            protectedLeg.Quantity,
            result.Success,
            result.OrderId,
            result.ErrorMessage,
            IsSquareOff: true,
            FilledQuantity: result.FilledQuantity,
            Price: protectedLeg.Price,
            OrderType: protectedLeg.OrderType,
            Status: result.Status,
            PriceReference: protectedLeg.PriceReference,
            AverageFillPrice: result.AverageFillPrice);

        await _executionSink.PublishAsync(
            new BasketExecutionEvent(
                Guid.NewGuid(),
                "manual-squareoff",
                position.TradingSymbol,
                position.TradingSymbol,
                "Manual square-off",
                new[] { legResult },
                MarginBlocked: false,
                BlockedReason: null,
                DateTimeOffset.UtcNow),
            cancellationToken);

        return Ok(result);
    }
}
