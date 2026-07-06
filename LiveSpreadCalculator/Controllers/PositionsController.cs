using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using LiveSpreadCalculator.Contracts;
using SpreadTrader.Core.Abstractions;
using SpreadTrader.Core.Models;
using SpreadTrader.Engine.Services;
using SpreadTrader.KiteIntegration.Instruments;
using SpreadTrader.KiteIntegration.Positions;

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
    private readonly KitePositionsService _positionsService;
    private readonly IOrderGateway _orderGateway;
    private readonly MarketProtectionService _marketProtection;
    private readonly InstrumentMasterService _instrumentMaster;
    private readonly IOrderExecutionSink _executionSink;
    private readonly ITriggerRuleStore _ruleStore;
    private readonly ILogger<PositionsController> _logger;

    public PositionsController(
        KitePositionsService positionsService,
        IOrderGateway orderGateway,
        MarketProtectionService marketProtection,
        InstrumentMasterService instrumentMaster,
        IOrderExecutionSink executionSink,
        ITriggerRuleStore ruleStore,
        ILogger<PositionsController> logger)
    {
        _positionsService = positionsService;
        _orderGateway = orderGateway;
        _marketProtection = marketProtection;
        _instrumentMaster = instrumentMaster;
        _executionSink = executionSink;
        _ruleStore = ruleStore;
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

        var leg = new OrderLegRequest
        {
            TradingSymbol = position.TradingSymbol,
            Exchange = position.Exchange,
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
        // auto-square-off go through the identical MarketProtectionService.Protect). Uses the
        // position's own last_price (from GET /portfolio/positions, already fetched above) as
        // the reference price rather than the live tick store, since a manually-held position
        // isn't necessarily for a symbol any currently-configured spread streams ticks for.
        var instrument = _instrumentMaster.Find(position.TradingSymbol, position.Exchange);
        var protectedLeg = _marketProtection.ProtectFromReference(
            leg,
            position.LastPrice,
            instrument?.TickSize ?? 0,
            $"Manual square-off {position.TradingSymbol}");

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
            Status: result.Status);

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

        // Only once the exit order actually went through - a rejected/failed square-off leaves
        // the position exactly as it was, so any exit trigger protecting it is still valid and
        // must stay armed. See TriggerRule.LinkedPositionTradingSymbol / AddPositionExitTriggerPage.jsx.
        if (result.Success)
            DeleteLinkedTriggers(position.TradingSymbol, position.Exchange, position.Product);

        return Ok(result);
    }

    /// <summary>Deletes every trigger rule explicitly linked to this exact position (symbol +
    /// exchange + product - see TriggerRule.LinkedPositionTradingSymbol) once it's been manually
    /// squared off. Deliberately only matches rules created via the "Add trigger" action on the
    /// Positions page, not every rule that happens to reference the same underlying/spread - an
    /// ordinary entry-style trigger from the Triggers page is untouched by a manual square-off,
    /// since it's not tied to this specific position at all.</summary>
    private void DeleteLinkedTriggers(string tradingSymbol, string exchange, string product)
    {
        var linked = _ruleStore.GetAll().Where(r =>
            string.Equals(r.LinkedPositionTradingSymbol, tradingSymbol, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.LinkedPositionExchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.LinkedPositionProduct, product, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var rule in linked)
        {
            _ruleStore.Remove(rule.Id);
            _logger.LogInformation(
                "Deleted exit trigger {RuleId} linked to {Symbol}/{Exchange}/{Product} - position was just manually squared off.",
                rule.Id, tradingSymbol, exchange, product);
        }
    }
}
