using System.Buffers.Binary;
using SpreadTrader.Core.Models;

namespace SpreadTrader.KiteIntegration.Ticker;

/// <summary>
/// Parses Kite's binary WebSocket tick protocol. Structure, per
/// https://kite.trade/docs/connect/v3/websocket/#binary-market-data :
///
///   message := int16 packetCount, then packetCount times [int16 length, bytes[length]]
///   packet (tradeable instrument, big-endian ints, prices in paise / 100):
///     0-4   instrument_token
///     4-8   last_price                 (ltp mode ends here: 8 bytes)
///     8-12  last_traded_quantity
///     12-16 average_traded_price
///     16-20 volume traded today
///     20-24 total buy quantity
///     24-28 total sell quantity
///     28-32 open
///     32-36 high
///     36-40 low
///     40-44 close                      (quote mode ends here: 44 bytes)
///     44-48 last traded timestamp
///     48-52 open interest
///     52-56 OI day high
///     56-60 OI day low
///     60-64 exchange timestamp
///     64-184 market depth - 5 bid entries [64-124] then 5 offer entries [124-184], each entry
///             being quantity(int32) + price(int32) + orders(int16) + 2 bytes padding = 12
///             bytes. Best bid is the price field of the first bid entry [68-72]; best ask is
///             the price field of the first offer entry [128-132]. Only present in "full" mode
///             (184-byte packets) - this is what BestBidPrice/BestAskPrice on Tick come from.
///
/// A 1-byte message is a heartbeat and carries no ticks. Index packets (NIFTY 50 etc.) use a
/// different, shorter layout and are intentionally not handled here - this parser targets
/// tradeable instruments (equities/futures), which is all a spread calculator needs.
/// Note: for currency-segment (CDS) instruments the divisor is 10,000,000, not 100 - not
/// handled here since this is aimed at equity/index futures (NFO).
/// </summary>
public static class KiteTickPacketParser
{
    public static IReadOnlyList<Tick> ParseMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < 2)
            return Array.Empty<Tick>();

        var packetCount = BinaryPrimitives.ReadInt16BigEndian(message);
        if (packetCount <= 0)
            return Array.Empty<Tick>();

        var result = new List<Tick>(packetCount);
        var offset = 2;

        for (var i = 0; i < packetCount; i++)
        {
            if (offset + 2 > message.Length) break;
            var packetLength = BinaryPrimitives.ReadInt16BigEndian(message.Slice(offset, 2));
            offset += 2;

            if (packetLength <= 0 || offset + packetLength > message.Length) break;
            var packet = message.Slice(offset, packetLength);
            offset += packetLength;

            if (TryParsePacket(packet, out var tick))
                result.Add(tick);
        }

        return result;
    }

    private static bool TryParsePacket(ReadOnlySpan<byte> packet, out Tick tick)
    {
        tick = default;
        if (packet.Length < 8)
            return false;

        var token = unchecked((uint)BinaryPrimitives.ReadInt32BigEndian(packet[..4]));
        var ltp = ReadPrice(packet.Slice(4, 4));
        var now = DateTimeOffset.UtcNow;

        if (packet.Length == 8)
        {
            tick = new Tick(token, ltp, 0, 0, 0, 0, 0, 0, 0, now, now);
            return true;
        }

        if (packet.Length < 44)
            return false; // unrecognized shape (e.g. index packet) - not handled by this parser

        var lastQty = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(8, 4));
        var volume = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(16, 4));
        var open = ReadPrice(packet.Slice(28, 4));
        var high = ReadPrice(packet.Slice(32, 4));
        var low = ReadPrice(packet.Slice(36, 4));
        var close = ReadPrice(packet.Slice(40, 4));

        long oi = 0;
        var exchangeTs = now;
        if (packet.Length >= 64)
        {
            oi = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(48, 4));
            var exchangeEpoch = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(60, 4));
            if (exchangeEpoch > 0)
                exchangeTs = DateTimeOffset.FromUnixTimeSeconds(exchangeEpoch);
        }

        decimal bestBid = 0;
        decimal bestAsk = 0;
        if (packet.Length >= 184)
        {
            bestBid = ReadPrice(packet.Slice(68, 4));  // first bid entry's price field
            bestAsk = ReadPrice(packet.Slice(128, 4)); // first offer entry's price field
        }

        tick = new Tick(token, ltp, lastQty, volume, open, high, low, close, oi, exchangeTs, now, bestBid, bestAsk);
        return true;
    }

    private static decimal ReadPrice(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes) / 100m;
}
