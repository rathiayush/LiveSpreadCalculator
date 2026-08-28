using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpreadTrader.Core.Brokers;

namespace SpreadTrader.DhanIntegration;

/// <summary>
/// Dhan's Market Quote LTP REST endpoint (POST /marketfeed/ltp), used only as CdvHostedService's
/// one-shot spot-price bootstrap - see ISpotPriceBootstrap's doc comment for why this must never
/// become an ongoing polling source. Request/response shapes confirmed against Dhan's own docs:
///   request:  { "&lt;exchangeSegment&gt;": [securityId, ...] }
///   response: { "data": { "&lt;exchangeSegment&gt;": { "&lt;securityId&gt;": { "last_price": n } } }, "status": "success" }
/// </summary>
public sealed class DhanQuoteService : ISpotPriceBootstrap
{
    private readonly DhanApiClient _api;
    private readonly ILogger<DhanQuoteService> _logger;

    public DhanQuoteService(DhanApiClient api, ILogger<DhanQuoteService> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async Task<decimal?> GetLastTradedPriceAsync(uint instrumentToken, string brokerSegment, CancellationToken cancellationToken)
    {
        if (!_api.HasSession)
        {
            _logger.LogWarning("CDV bootstrap: cannot fetch LTP for {Segment}:{Token} - Dhan is not authenticated.", brokerSegment, instrumentToken);
            return null;
        }

        var payload = new Dictionary<string, uint[]> { [brokerSegment] = new[] { instrumentToken } };
        using var response = await _api.SendAsync(
            HttpMethod.Post, "/marketfeed/ltp", payload,
            $"CDV spot bootstrap ({brokerSegment}:{instrumentToken})", cancellationToken);

        if (!response.Success)
        {
            _logger.LogWarning("CDV bootstrap: LTP request failed for {Segment}:{Token} - {Error}", brokerSegment, instrumentToken, response.ErrorMessage);
            return null;
        }

        if (!response.Root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty(brokerSegment, out var segmentData) ||
            !segmentData.TryGetProperty(instrumentToken.ToString(), out var instrumentData) ||
            !instrumentData.TryGetProperty("last_price", out var lastPrice) ||
            lastPrice.ValueKind is not (JsonValueKind.Number))
        {
            _logger.LogWarning("CDV bootstrap: LTP response for {Segment}:{Token} had no usable last_price - {Body}",
                brokerSegment, instrumentToken, response.Root.ToString());
            return null;
        }

        var price = lastPrice.GetDecimal();
        return price > 0 ? price : null;
    }
}
