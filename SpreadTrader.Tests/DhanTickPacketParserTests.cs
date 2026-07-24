using System.Buffers.Binary;
using SpreadTrader.DhanIntegration.Ticker;

namespace SpreadTrader.Tests;

/// <summary>
/// Byte-level tests for Dhan's binary market-data protocol.
///
/// These exist because this parser is the one piece of the Dhan integration that cannot be
/// verified by reading a response body: it decodes a packed binary frame, and every plausible
/// mistake in it (wrong endianness, reading a float as an int, an off-by-four offset, applying
/// Kite's paise-to-rupee division) produces numbers that look perfectly reasonable but are wrong.
/// A wrong best-bid/ask silently feeds the spread calculation and can fire a real order at a price
/// nobody intended, so the layout is pinned here rather than trusted.
///
/// Each test builds a packet to Dhan's documented v2 layout and asserts the parser recovers
/// exactly what was written - see DhanTickPacketParser's doc comment for the field offsets.
/// </summary>
public class DhanTickPacketParserTests
{
    private const byte NseFnoSegment = 2;

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>Writes the 8-byte header every Dhan packet starts with: response code, total
    /// message length (header included), exchange segment, security id - all little-endian.</summary>
    private static void WriteHeader(Span<byte> packet, byte responseCode, ushort length, byte segment, uint securityId)
    {
        packet[0] = responseCode;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(1, 2), length);
        packet[3] = segment;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(4, 4), securityId);
    }

    private static void WriteFloat(Span<byte> packet, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(offset, 4), value);

    private static void WriteUInt32(Span<byte> packet, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(offset, 4), value);

    /// <summary>Builds a 162-byte Full packet - the only kind carrying market depth, and therefore
    /// the only one that can drive spread evaluation.</summary>
    private static byte[] BuildFullPacket(
        uint securityId,
        float ltp,
        float open, float close, float high, float low,
        uint volume, uint openInterest,
        float bestBid, float bestAsk,
        ushort lastTradedQty = 0,
        uint lastTradedTime = 0)
    {
        var packet = new byte[162];
        var span = packet.AsSpan();

        WriteHeader(span, responseCode: 8, length: 162, segment: NseFnoSegment, securityId: securityId);
        WriteFloat(span, 8, ltp);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), lastTradedQty);
        WriteUInt32(span, 14, lastTradedTime);
        WriteFloat(span, 18, 0f);              // average traded price
        WriteUInt32(span, 22, volume);
        WriteUInt32(span, 26, 0);              // total sell quantity
        WriteUInt32(span, 30, 0);              // total buy quantity
        WriteUInt32(span, 34, openInterest);
        WriteUInt32(span, 38, 0);              // OI day high
        WriteUInt32(span, 42, 0);              // OI day low
        WriteFloat(span, 46, open);
        WriteFloat(span, 50, close);
        WriteFloat(span, 54, high);
        WriteFloat(span, 58, low);

        // Depth block: 5 entries x 20 bytes from offset 62. Only the FIRST entry is the best
        // bid/ask; the rest are deeper levels and must not be mistaken for it.
        WriteUInt32(span, 62, 25);             // bid quantity
        WriteUInt32(span, 66, 50);             // ask quantity
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(70, 2), 3);  // bid orders
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(72, 2), 4);  // ask orders
        WriteFloat(span, 74, bestBid);
        WriteFloat(span, 78, bestAsk);

        // Second depth level, deliberately far away from the touch - if the parser ever reads the
        // wrong entry, these values make it obvious rather than plausible.
        WriteFloat(span, 82 + 12, bestBid - 5f);
        WriteFloat(span, 82 + 16, bestAsk + 5f);

        return packet;
    }

    // ---- tests -----------------------------------------------------------------------------------

    [Fact]
    public void FullPacket_DecodesEveryField()
    {
        var packet = BuildFullPacket(
            securityId: 49081,
            ltp: 2456.75f,
            open: 2440.10f, close: 2438.55f, high: 2470.00f, low: 2431.25f,
            volume: 187_500, openInterest: 4_212_000,
            bestBid: 2456.50f, bestAsk: 2457.00f,
            lastTradedQty: 250, lastTradedTime: 1_753_000_000);

        var decoded = Assert.Single(DhanTickPacketParser.ParseMessage(packet));

        Assert.Equal(DhanPacketKind.Full, decoded.Kind);
        Assert.Equal(49081u, decoded.SecurityId);
        Assert.Equal(NseFnoSegment, decoded.ExchangeSegment);

        // Prices are float32 rupees - NOT paise. Applying Kite's /100 here would yield 24.5675.
        Assert.Equal(2456.75m, decoded.LastPrice, 2);
        Assert.Equal(2456.50m, decoded.BestBidPrice, 2);
        Assert.Equal(2457.00m, decoded.BestAskPrice, 2);

        // Dhan's OHLC block is ordered open, CLOSE, high, low - a natural open/high/low/close
        // assumption would silently swap close and high here.
        Assert.Equal(2440.10m, decoded.Open, 2);
        Assert.Equal(2438.55m, decoded.Close, 2);
        Assert.Equal(2470.00m, decoded.High, 2);
        Assert.Equal(2431.25m, decoded.Low, 2);

        Assert.Equal(187_500, decoded.Volume);
        Assert.Equal(4_212_000, decoded.OpenInterest);
        Assert.Equal(250, decoded.LastTradedQuantity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_753_000_000), decoded.ExchangeTimestamp);
    }

    [Fact]
    public void FullPacket_BestBidAskComeFromTheFirstDepthLevel()
    {
        // The touch must be the FIRST depth entry, not the deepest or the last.
        var packet = BuildFullPacket(
            securityId: 1, ltp: 100f, open: 0f, close: 0f, high: 0f, low: 0f,
            volume: 0, openInterest: 0, bestBid: 99.95f, bestAsk: 100.05f);

        var decoded = Assert.Single(DhanTickPacketParser.ParseMessage(packet));

        Assert.Equal(99.95m, decoded.BestBidPrice, 2);
        Assert.Equal(100.05m, decoded.BestAskPrice, 2);
        // The level-2 prices written 5 rupees away must not have leaked into the touch.
        Assert.NotEqual(94.95m, decoded.BestBidPrice);
        Assert.NotEqual(105.05m, decoded.BestAskPrice);
    }

    [Fact]
    public void OneFrame_CarryingSeveralPackets_DecodesAllOfThem()
    {
        // Dhan packs multiple instrument updates into a single WebSocket frame. The reference
        // Python client only ever reads the first, which would silently drop the second leg of a
        // spread - and a spread priced off one stale leg is exactly the failure this must not have.
        var first = BuildFullPacket(
            securityId: 111, ltp: 10f, open: 0, close: 0, high: 0, low: 0,
            volume: 0, openInterest: 0, bestBid: 9.9f, bestAsk: 10.1f);
        var second = BuildFullPacket(
            securityId: 222, ltp: 20f, open: 0, close: 0, high: 0, low: 0,
            volume: 0, openInterest: 0, bestBid: 19.9f, bestAsk: 20.1f);

        var frame = first.Concat(second).ToArray();
        var decoded = DhanTickPacketParser.ParseMessage(frame);

        Assert.Equal(2, decoded.Count);
        Assert.Equal(111u, decoded[0].SecurityId);
        Assert.Equal(9.9m, decoded[0].BestBidPrice, 2);
        Assert.Equal(222u, decoded[1].SecurityId);
        Assert.Equal(20.1m, decoded[1].BestAskPrice, 2);
    }

    [Fact]
    public void MixedFrame_OfDifferentPacketKinds_DecodesEach()
    {
        var full = BuildFullPacket(
            securityId: 500, ltp: 55f, open: 0, close: 0, high: 0, low: 0,
            volume: 0, openInterest: 0, bestBid: 54.9f, bestAsk: 55.1f);

        // Ticker packet (code 2, 16 bytes): LTP + last traded time only, no depth.
        var ticker = new byte[16];
        WriteHeader(ticker, responseCode: 2, length: 16, segment: NseFnoSegment, securityId: 501);
        WriteFloat(ticker, 8, 77.25f);
        WriteUInt32(ticker, 12, 1_753_000_100);

        var decoded = DhanTickPacketParser.ParseMessage(full.Concat(ticker).ToArray());

        Assert.Equal(2, decoded.Count);
        Assert.Equal(DhanPacketKind.Full, decoded[0].Kind);
        Assert.Equal(DhanPacketKind.Ticker, decoded[1].Kind);
        Assert.Equal(77.25m, decoded[1].LastPrice, 2);
        // A ticker packet has no depth at all - it must report zero rather than garbage, so the
        // feed client knows to carry the previous depth forward instead of publishing a blank book.
        Assert.Equal(0m, decoded[1].BestBidPrice);
        Assert.Equal(0m, decoded[1].BestAskPrice);
    }

    [Fact]
    public void QuotePacket_DecodesOhlcInDhanOrder()
    {
        var packet = new byte[50];
        var span = packet.AsSpan();
        WriteHeader(span, responseCode: 4, length: 50, segment: NseFnoSegment, securityId: 42);
        WriteFloat(span, 8, 101.5f);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), 10);
        WriteUInt32(span, 14, 1_753_000_200);
        WriteFloat(span, 18, 101.0f);   // average traded price
        WriteUInt32(span, 22, 9_000);   // volume
        WriteFloat(span, 34, 100.0f);   // open
        WriteFloat(span, 38, 99.0f);    // close
        WriteFloat(span, 42, 103.0f);   // high
        WriteFloat(span, 46, 98.0f);    // low

        var decoded = Assert.Single(DhanTickPacketParser.ParseMessage(packet));

        Assert.Equal(DhanPacketKind.Quote, decoded.Kind);
        Assert.Equal(101.5m, decoded.LastPrice, 2);
        Assert.Equal(100.0m, decoded.Open, 2);
        Assert.Equal(99.0m, decoded.Close, 2);
        Assert.Equal(103.0m, decoded.High, 2);
        Assert.Equal(98.0m, decoded.Low, 2);
        Assert.Equal(9_000, decoded.Volume);
    }

    [Fact]
    public void DisconnectPacket_ReportsTheReasonCode()
    {
        // 807 is "access token expired" - the code DhanAuthFailureHandler acts on to clear the
        // session and prompt a re-login, instead of reconnect-looping forever against a dead token.
        var packet = new byte[10];
        var span = packet.AsSpan();
        WriteHeader(span, responseCode: 50, length: 10, segment: 0, securityId: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), 807);

        var decoded = Assert.Single(DhanTickPacketParser.ParseMessage(packet));

        Assert.Equal(DhanPacketKind.Disconnect, decoded.Kind);
        Assert.Equal(807, decoded.DisconnectCode);
    }

    [Theory]
    [InlineData(0)]    // empty frame
    [InlineData(3)]    // shorter than even the 8-byte header
    [InlineData(20)]   // header claims a Full packet but the body is truncated
    public void MalformedFrames_AreIgnoredRatherThanThrowing(int length)
    {
        // A malformed frame must never take down the receive loop - that would stop the tick feed
        // for every instrument over one bad packet.
        var frame = new byte[length];
        if (length >= 8)
            WriteHeader(frame, responseCode: 8, length: 162, segment: NseFnoSegment, securityId: 7);

        var decoded = DhanTickPacketParser.ParseMessage(frame);

        Assert.Empty(decoded);
    }

    [Fact]
    public void TrailingGarbageAfterAValidPacket_StillYieldsThatPacket()
    {
        var valid = BuildFullPacket(
            securityId: 900, ltp: 12f, open: 0, close: 0, high: 0, low: 0,
            volume: 0, openInterest: 0, bestBid: 11.9f, bestAsk: 12.1f);

        var frame = valid.Concat(new byte[5]).ToArray(); // truncated tail

        var decoded = DhanTickPacketParser.ParseMessage(frame);

        Assert.Single(decoded);
        Assert.Equal(900u, decoded[0].SecurityId);
    }

    [Fact]
    public void NonPositiveAndNonFinitePrices_AreNormalizedToZero()
    {
        // An empty depth slot decodes to 0 or NaN. Either must come through as 0 so it can never
        // propagate into spread arithmetic as a real-looking price.
        var packet = BuildFullPacket(
            securityId: 3, ltp: float.NaN, open: 0, close: 0, high: 0, low: 0,
            volume: 0, openInterest: 0, bestBid: 0f, bestAsk: float.NegativeInfinity);

        var decoded = Assert.Single(DhanTickPacketParser.ParseMessage(packet));

        Assert.Equal(0m, decoded.LastPrice);
        Assert.Equal(0m, decoded.BestBidPrice);
        Assert.Equal(0m, decoded.BestAskPrice);
    }
}
