using System.Buffers.Binary;

namespace SpreadTrader.DhanIntegration.Ticker;

/// <summary>What a Dhan market-data packet carries, derived from its response code.</summary>
public enum DhanPacketKind
{
    Unknown = 0,
    Ticker = 2,
    Depth = 3,
    Quote = 4,
    OpenInterest = 5,
    PreviousClose = 6,
    MarketStatus = 7,
    Full = 8,
    Disconnect = 50,
}

/// <summary>One decoded packet. A struct so the hot receive path doesn't allocate per packet.
/// Fields not carried by the packet's own kind are left at zero - see DhanMarketFeedClient, which
/// is what decides how each kind folds into the shared Tick for that instrument.</summary>
public readonly record struct DhanPacket(
    DhanPacketKind Kind,
    uint SecurityId,
    byte ExchangeSegment,
    decimal LastPrice,
    long LastTradedQuantity,
    long Volume,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long OpenInterest,
    decimal BestBidPrice,
    decimal BestAskPrice,
    DateTimeOffset ExchangeTimestamp,
    int DisconnectCode);

/// <summary>
/// Parses DhanHQ v2's binary market-data protocol.
///
/// This differs from Kite's binary protocol in three ways that matter, and getting any of them
/// wrong yields plausible-looking but wrong prices rather than an obvious failure:
///
///  1. ENDIANNESS. Dhan is LITTLE-endian; Kite is big-endian.
///  2. PRICE ENCODING. Dhan sends IEEE-754 float32 values already denominated in rupees. Kite
///     sends int32 paise that must be divided by 100. Applying Kite's /100 to a Dhan price - or
///     reading Dhan's bytes as an integer - produces silently wrong numbers, so prices here are
///     read as floats and converted straight to decimal.
///  3. FRAMING. Kite frames a message as "packet count, then length-prefixed packets". Dhan puts
///     the length of each packet in its own 8-byte header, and a single WebSocket frame may carry
///     several packets back to back. So this walks the buffer by each packet's declared
///     messageLength rather than assuming one packet per frame (which is what the reference Python
///     client assumes, and why it can drop updates when several arrive together).
///
/// Packet header, common to every response code (offsets in bytes):
///     0     byte    response code (see DhanPacketKind)
///     1-3   uint16  message length, INCLUDING this 8-byte header
///     3     byte    exchange segment (see DhanSymbology.FeedSegmentToExchange)
///     4-8   uint32  security id
///
/// Ticker (code 2, 16 bytes):
///     8-12  float32 last traded price
///     12-16 uint32  last traded time (epoch seconds)
///
/// Quote (code 4, 50 bytes):
///     8-12  float32 last traded price      12-14 uint16  last traded quantity
///     14-18 uint32  last traded time       18-22 float32 average traded price
///     22-26 uint32  volume                 26-30 uint32  total sell quantity
///     30-34 uint32  total buy quantity     34-38 float32 open
///     38-42 float32 close                  42-46 float32 high
///     46-50 float32 low
///
/// Full (code 8, 162 bytes) - the only packet carrying market depth, which is why the feed
/// subscribes in Full mode (request code 21); spread triggers are evaluated against best bid/ask:
///     8-12  float32 last traded price      12-14 uint16  last traded quantity
///     14-18 uint32  last traded time       18-22 float32 average traded price
///     22-26 uint32  volume                 26-30 uint32  total sell quantity
///     30-34 uint32  total buy quantity     34-38 uint32  open interest
///     38-42 uint32  OI day high            42-46 uint32  OI day low
///     46-50 float32 open                   50-54 float32 close
///     54-58 float32 high                   58-62 float32 low
///     62-162        market depth: 5 entries x 20 bytes
///
/// Market depth (code 3, 112 bytes) carries LTP at 8-12 and the same 5x20 depth block at 12-112.
///
/// Each depth entry is:
///     0-4   uint32  bid quantity           4-8   uint32  ask quantity
///     8-10  uint16  bid orders             10-12 uint16  ask orders
///     12-16 float32 bid price              16-20 float32 ask price
/// so the best bid/ask prices are the first entry's price fields - offsets 74 and 78 in a Full
/// packet, 24 and 28 in a Depth packet.
///
/// Note Dhan's ordering of the OHLC block is open, CLOSE, high, low - not the open/high/low/close
/// order the field names might suggest.
/// </summary>
public static class DhanTickPacketParser
{
    private const int HeaderLength = 8;

    // Offset of the depth block within each packet kind that carries one.
    private const int FullDepthOffset = 62;
    private const int DepthPacketDepthOffset = 12;
    private const int DepthEntryLength = 20;

    /// <summary>Expected total length per response code, used only as a fallback when a packet
    /// declares a nonsensical messageLength (0, or longer than the buffer). Codes absent here have
    /// no fixed size we rely on.</summary>
    private static int KnownLength(DhanPacketKind kind) => kind switch
    {
        DhanPacketKind.Ticker => 16,
        DhanPacketKind.Depth => 112,
        DhanPacketKind.Quote => 50,
        DhanPacketKind.OpenInterest => 12,
        DhanPacketKind.PreviousClose => 16,
        DhanPacketKind.MarketStatus => 8,
        DhanPacketKind.Full => 162,
        DhanPacketKind.Disconnect => 10,
        _ => 0,
    };

    /// <summary>Decodes every packet in one WebSocket frame. Stops at the first malformed packet
    /// rather than throwing - a truncated or unrecognised frame must never take down the receive
    /// loop, and whatever was decoded before it is still valid.</summary>
    public static IReadOnlyList<DhanPacket> ParseMessage(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
            return Array.Empty<DhanPacket>();

        var packets = new List<DhanPacket>(1);
        var offset = 0;

        while (offset + HeaderLength <= message.Length)
        {
            var kind = (DhanPacketKind)message[offset];
            var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(message.Slice(offset + 1, 2));

            var length = declaredLength;
            if (length < HeaderLength || offset + length > message.Length)
            {
                // Fall back to the documented size for this code; if that doesn't fit either, the
                // frame is not something we can walk any further.
                var known = KnownLength(kind);
                if (known >= HeaderLength && offset + known <= message.Length)
                    length = (ushort)known;
                else
                    break;
            }

            if (TryParsePacket(message.Slice(offset, length), kind, out var packet))
                packets.Add(packet);

            offset += length;
        }

        return packets;
    }

    private static bool TryParsePacket(ReadOnlySpan<byte> packet, DhanPacketKind kind, out DhanPacket result)
    {
        result = default;
        if (packet.Length < HeaderLength) return false;

        var exchangeSegment = packet[3];
        var securityId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4));
        var now = DateTimeOffset.UtcNow;

        switch (kind)
        {
            case DhanPacketKind.Ticker:
            {
                if (packet.Length < 16) return false;
                result = New(kind, securityId, exchangeSegment) with
                {
                    LastPrice = ReadPrice(packet.Slice(8, 4)),
                    ExchangeTimestamp = ReadEpoch(packet.Slice(12, 4), now),
                };
                return true;
            }

            case DhanPacketKind.Quote:
            {
                if (packet.Length < 50) return false;
                result = New(kind, securityId, exchangeSegment) with
                {
                    LastPrice = ReadPrice(packet.Slice(8, 4)),
                    LastTradedQuantity = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2)),
                    ExchangeTimestamp = ReadEpoch(packet.Slice(14, 4), now),
                    Volume = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(22, 4)),
                    Open = ReadPrice(packet.Slice(34, 4)),
                    Close = ReadPrice(packet.Slice(38, 4)),
                    High = ReadPrice(packet.Slice(42, 4)),
                    Low = ReadPrice(packet.Slice(46, 4)),
                };
                return true;
            }

            case DhanPacketKind.Full:
            {
                if (packet.Length < 162) return false;
                var (bid, ask) = ReadBestBidAsk(packet, FullDepthOffset);
                result = New(kind, securityId, exchangeSegment) with
                {
                    LastPrice = ReadPrice(packet.Slice(8, 4)),
                    LastTradedQuantity = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(12, 2)),
                    ExchangeTimestamp = ReadEpoch(packet.Slice(14, 4), now),
                    Volume = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(22, 4)),
                    OpenInterest = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(34, 4)),
                    Open = ReadPrice(packet.Slice(46, 4)),
                    Close = ReadPrice(packet.Slice(50, 4)),
                    High = ReadPrice(packet.Slice(54, 4)),
                    Low = ReadPrice(packet.Slice(58, 4)),
                    BestBidPrice = bid,
                    BestAskPrice = ask,
                };
                return true;
            }

            case DhanPacketKind.Depth:
            {
                if (packet.Length < 112) return false;
                var (bid, ask) = ReadBestBidAsk(packet, DepthPacketDepthOffset);
                result = New(kind, securityId, exchangeSegment) with
                {
                    LastPrice = ReadPrice(packet.Slice(8, 4)),
                    BestBidPrice = bid,
                    BestAskPrice = ask,
                };
                return true;
            }

            case DhanPacketKind.OpenInterest:
            {
                if (packet.Length < 12) return false;
                result = New(kind, securityId, exchangeSegment) with
                {
                    OpenInterest = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4)),
                };
                return true;
            }

            case DhanPacketKind.PreviousClose:
            {
                if (packet.Length < 16) return false;
                result = New(kind, securityId, exchangeSegment) with
                {
                    Close = ReadPrice(packet.Slice(8, 4)),
                };
                return true;
            }

            case DhanPacketKind.Disconnect:
            {
                if (packet.Length < 10) return false;
                result = New(kind, securityId, exchangeSegment) with
                {
                    DisconnectCode = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2)),
                };
                return true;
            }

            case DhanPacketKind.MarketStatus:
                result = New(kind, securityId, exchangeSegment);
                return true;

            default:
                return false; // a response code Dhan has added since - ignored, not fatal
        }
    }

    private static DhanPacket New(DhanPacketKind kind, uint securityId, byte exchangeSegment) =>
        new(kind, securityId, exchangeSegment, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow, 0);

    /// <summary>Best bid/ask are the price fields of the FIRST of the five depth entries.</summary>
    private static (decimal Bid, decimal Ask) ReadBestBidAsk(ReadOnlySpan<byte> packet, int depthOffset)
    {
        if (packet.Length < depthOffset + DepthEntryLength) return (0m, 0m);
        var bid = ReadPrice(packet.Slice(depthOffset + 12, 4));
        var ask = ReadPrice(packet.Slice(depthOffset + 16, 4));
        return (bid, ask);
    }

    /// <summary>Dhan prices are float32 in rupees. NaN/Infinity (which a zeroed or padded depth
    /// slot can decode to) is normalized to 0 so it can never propagate into spread arithmetic.
    /// </summary>
    private static decimal ReadPrice(ReadOnlySpan<byte> bytes)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return 0m;

        // Guard the decimal conversion: a garbage float outside decimal's range would throw on the
        // receive loop, which must never happen for one bad packet.
        return value > 1e12f ? 0m : (decimal)value;
    }

    private static DateTimeOffset ReadEpoch(ReadOnlySpan<byte> bytes, DateTimeOffset fallback)
    {
        var epoch = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return epoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(epoch) : fallback;
    }
}
