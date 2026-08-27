namespace SpreadTrader.DhanIntegration;

/// <summary>
/// The single translation table between this app's canonical vocabulary and Dhan's.
///
/// The app was built against Kite, so its canonical terms are Kite's: exchange codes "NFO"/"MCX",
/// products "NRML"/"MIS", order statuses "COMPLETE"/"REJECTED"/"OPEN". Rather than rewrite every
/// engine service, CSV column, trigger rule and dashboard label to a third neutral vocabulary -
/// which would have been a far larger and riskier change for no behavioural gain - the Dhan
/// adapter translates at its own boundary and speaks the canonical vocabulary everywhere else.
/// Everything Dhan-specific therefore lives in exactly this one file plus the six adapter classes
/// that use it.
///
/// Keeping the mapping here (rather than inline in each adapter) is also what makes the mapping
/// reviewable: an incorrect product or segment mapping silently places a real order against the
/// wrong contract or the wrong margin bucket, so it is worth being able to read the whole table
/// in one place.
/// </summary>
public static class DhanSymbology
{
    // ---- Exchange segments ---------------------------------------------------------------------
    // Canonical (Kite-style) exchange code -> Dhan exchangeSegment string, used in REST payloads
    // and WebSocket subscription messages.
    private static readonly Dictionary<string, string> CanonicalToSegment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NFO"] = "NSE_FNO",
        ["MCX"] = "MCX_COMM",
        ["NSE"] = "NSE_EQ",
        ["BSE"] = "BSE_EQ",
        ["BFO"] = "BSE_FNO",
        ["CDS"] = "NSE_CURRENCY",
        ["BCD"] = "BSE_CURRENCY",
    };

    private static readonly Dictionary<string, string> SegmentToCanonical =
        CanonicalToSegment.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Dhan's numeric exchange-segment codes, as they appear in the single byte at offset
    /// 3 of every binary market-data packet. Mapped straight to the canonical exchange code.</summary>
    private static readonly Dictionary<byte, string> FeedSegmentToCanonical = new()
    {
        [0] = "IDX",  // IDX_I  - index, not tradeable directly; subscribed for spot pricing since the
                      // Options Flow / spot-price features - see IndexSegmentTokenFlag below for why
                      // that made this segment's ids no longer safe to key a tick store by alone.
        [1] = "NSE",  // NSE_EQ
        [2] = "NFO",  // NSE_FNO
        [3] = "CDS",  // NSE_CURRENCY
        [4] = "BSE",  // BSE_EQ
        [5] = "MCX",  // MCX_COMM
        [7] = "BCD",  // BSE_CURRENCY
        [8] = "BFO",  // BSE_FNO
    };

    /// <summary>"NFO" -> "NSE_FNO". Falls back to returning the input unchanged when it is already
    /// a Dhan segment string, so a value that has been round-tripped is never mangled.</summary>
    public static string ToDhanSegment(string canonicalExchange) =>
        CanonicalToSegment.TryGetValue(canonicalExchange, out var segment) ? segment : canonicalExchange;

    /// <summary>"NSE_FNO" -> "NFO". Falls back to returning the input unchanged, which is what
    /// makes an unexpected new Dhan segment show up in logs/positions as itself rather than being
    /// silently mapped onto the wrong exchange.</summary>
    public static string ToCanonicalExchange(string dhanSegment) =>
        SegmentToCanonical.TryGetValue(dhanSegment, out var exchange) ? exchange : dhanSegment;

    /// <summary>Binary feed segment byte -> canonical exchange code, or null for a code Dhan has
    /// added since this table was written.</summary>
    public static string? FeedSegmentToExchange(byte feedSegment) =>
        FeedSegmentToCanonical.TryGetValue(feedSegment, out var exchange) ? exchange : null;

    // ---- Products ------------------------------------------------------------------------------
    /// <summary>Canonical (Kite) product -> Dhan productType.
    ///
    /// The important one is NRML -> MARGIN: on Kite, NRML is the carry-forward F&amp;O product; Dhan
    /// calls the same thing MARGIN. Mapping it to Dhan's "CNC" instead would be a delivery-equity
    /// product and simply invalid for a futures contract, and mapping it to INTRADAY would silently
    /// convert every position into one the broker auto-squares off at session close - which for a
    /// calendar spread held across days is a materially different (and wrong) trade.</summary>
    private static readonly Dictionary<string, string> CanonicalToProduct = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NRML"] = "MARGIN",
        ["MIS"] = "INTRADAY",
        ["CNC"] = "CNC",
        ["MTF"] = "MTF",
        ["CO"] = "CO",
        ["BO"] = "BO",
    };

    private static readonly Dictionary<string, string> ProductToCanonical =
        CanonicalToProduct.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string ToDhanProduct(string canonicalProduct) =>
        CanonicalToProduct.TryGetValue(canonicalProduct, out var product) ? product : canonicalProduct.ToUpperInvariant();

    /// <summary>Dhan productType -> canonical, so a position fetched back from Dhan reports the
    /// same product string the dashboard's square-off form will send back down (positions are
    /// netted per product, so these must agree exactly).</summary>
    public static string ToCanonicalProduct(string dhanProduct) =>
        ProductToCanonical.TryGetValue(dhanProduct, out var product) ? product : dhanProduct.ToUpperInvariant();

    // ---- Order types ---------------------------------------------------------------------------
    private static readonly Dictionary<string, string> CanonicalToOrderType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MARKET"] = "MARKET",
        ["LIMIT"] = "LIMIT",
        ["SL"] = "STOP_LOSS",
        ["SL-M"] = "STOP_LOSS_MARKET",
    };

    public static string ToDhanOrderType(string canonicalOrderType) =>
        CanonicalToOrderType.TryGetValue(canonicalOrderType, out var orderType) ? orderType : canonicalOrderType.ToUpperInvariant();

    // ---- Order status --------------------------------------------------------------------------
    /// <summary>Dhan orderStatus -> the canonical (Kite-flavoured) status the engine already
    /// branches on. OrderDispatchHostedService treats "OPEN" as "accepted but not yet resolved -
    /// cancel it if it stays that way", and everything else as terminal, so the mapping has to put
    /// each Dhan status on the right side of that line:
    ///
    ///   TRANSIT     - accepted by Dhan, not yet at the exchange        -> OPEN (still in flight)
    ///   PENDING     - resting at the exchange, unfilled                -> OPEN (still in flight)
    ///   TRIGGERED   - a stop order's trigger hit; now working          -> OPEN (still in flight)
    ///   PART_TRADED - partially filled, remainder still resting        -> OPEN (still in flight)
    ///   TRADED      - filled                                           -> COMPLETE
    ///   REJECTED    - rejected by Dhan or the exchange                 -> REJECTED
    ///   CANCELLED   - cancelled (including an IOC's unfilled residue)  -> CANCELLED
    ///   EXPIRED     - lapsed at session end without filling            -> CANCELLED
    ///   CLOSED      - closed out by Dhan without reaching the book     -> CLOSED (terminal)
    ///
    /// EXPIRED maps to CANCELLED rather than getting its own value because the engine only cares
    /// about the terminal/non-terminal distinction plus whether anything filled, and an expired
    /// order is a cancelled one for both purposes.
    ///
    /// TRIGGERED and CLOSED are both in Dhan's documented order-status enum but were missing here,
    /// so they fell through to the passthrough arm and were classified non-terminal. That is right
    /// for TRIGGERED (the order really is still working) but wrong for CLOSED, which never
    /// progresses: the gateway burned its full status-poll budget on it and then reported the
    /// order as unconfirmed/"may still be open" when it was already finished. CLOSED keeps its own
    /// canonical name rather than being folded into COMPLETE, so a CLOSED order with no fill isn't
    /// described to the operator as having completed - Success is decided by filled quantity
    /// either way (see DhanOrderGateway.PollOrderStatusAsync).</summary>
    public static string ToCanonicalOrderStatus(string? dhanStatus) => dhanStatus?.ToUpperInvariant() switch
    {
        "TRADED" => "COMPLETE",
        "REJECTED" => "REJECTED",
        "CANCELLED" or "CANCELED" or "EXPIRED" => "CANCELLED",
        "CLOSED" => "CLOSED",
        "TRANSIT" or "PENDING" or "TRIGGERED" or "PART_TRADED" or "PARTIALLY_TRADED" => "OPEN",
        null or "" => "OPEN",
        _ => dhanStatus!.ToUpperInvariant(),
    };

    /// <summary>Statuses after which polling an order again cannot change the answer.</summary>
    public static bool IsTerminalStatus(string canonicalStatus) =>
        canonicalStatus is "COMPLETE" or "REJECTED" or "CANCELLED" or "CLOSED";

    // ---- Market-feed error codes ---------------------------------------------------------------

    /// <summary>
    /// Dhan's Data API error codes, as carried by the reason field of a market-feed disconnect
    /// packet (response code 50). Transcribed from Dhan's published annexure.
    ///
    /// Three of these were previously mapped to the wrong meanings (808 was described as "invalid
    /// client id", 809 as "authentication failed", and 810 was absent entirely), which mattered
    /// beyond the log text: 809 and 810 are dead-session conditions, so misreading them as generic
    /// failures left the feed reconnecting forever against credentials that could never work - the
    /// exact failure mode DhanAuthFailureHandler exists to prevent. See IsSessionInvalidCode.
    /// </summary>
    public static string DescribeFeedErrorCode(int code) => code switch
    {
        800 => "internal server error at Dhan",
        804 => "the subscription requested more instruments than the connection allows",
        805 => "too many requests or connections for this account - further ones may be blocked",
        806 => "the account is not subscribed to Dhan's Data APIs",
        807 => "the access token has expired",
        808 => "authentication failed - the credentials were not accepted",
        809 => "the access token is invalid",
        810 => "the client id is invalid",
        811 => "invalid expiry date",
        812 => "invalid date format",
        813 => "invalid security id",
        814 => "invalid request",
        _ => "unspecified reason",
    };

    /// <summary>
    /// True for the disconnect codes that mean this session's credentials will never work again,
    /// so the only useful response is to clear the session and ask the operator to log in - not to
    /// keep reconnecting.
    ///
    /// Deliberately excludes 805 (too many connections) and 806 (Data APIs not subscribed): both
    /// are real problems, but the token is fine, and logging the operator out over either would
    /// hide the actual cause behind a login prompt that cannot fix it.
    /// </summary>
    public static bool IsSessionInvalidCode(int code) => code is 807 or 808 or 809 or 810;

    // ---- Cross-segment token disambiguation -----------------------------------------------------
    /// <summary>
    /// Dhan's securityId is only unique WITHIN one exchange segment - the comment this table used to
    /// carry on feed segment 0 ("IDX_I - index, not tradeable; never subscribed by this app") was
    /// true when it was written, but stopped being true the moment the Options Flow / spot-price
    /// features started subscribing NIFTY/BANKNIFTY's index-segment spot tokens alongside every
    /// other futures/equity/option token on the SAME connection. ITickStore, DhanMarketFeedClient's
    /// _lastTick, and _subscribedTokens all key purely by a bare uint InstrumentToken with no segment
    /// component - so if an index token's raw numeric id happens to collide with some other actively
    /// subscribed contract's id in a different segment (NSE_FNO, NSE_EQ, ...), their ticks silently
    /// overwrite each other. That is exactly what produced a plausible-looking but wrong ATM for
    /// NIFTY/BANKNIFTY in production: the "spot" price being read was really some other instrument's.
    ///
    /// Rather than widen every consumer's key to (segment, id) - a change that would ripple through
    /// every hosted service, the SignalR/Dashboard contract, and every already-resolved token in a
    /// running process - this reserves one otherwise-unused high bit of the uint token space for
    /// index-segment (IDX_I) instruments only, which is the sole segment this app subscribes that was
    /// never accounted for when the shared token space was designed. Every real Dhan security id is
    /// documented to fit comfortably under 2^24, so this bit can never collide with a genuine id.
    ///
    /// Used at exactly two boundaries, which must agree: DhanInstrumentMasterService tags an index
    /// row's token the moment it is constructed from the scrip master, and DhanTickPacketParser tags
    /// a packet's SecurityId the moment it is decoded off the wire (based on the packet's own
    /// ExchangeSegment byte) - after that, every C#-side consumer (subscribe, tick store, reconcile)
    /// sees one consistent, collision-free token. The tag is stripped back off in exactly one place,
    /// DhanMarketFeedClient.ResolveSubscriptionEntries, which is the one spot the RAW numeric id must
    /// go back out onto the wire in Dhan's own subscribe payload.
    /// </summary>
    private const uint IndexSegmentTokenFlag = 0x4000_0000;

    public static uint TagIndexToken(uint rawSecurityId) => rawSecurityId | IndexSegmentTokenFlag;

    public static uint UntagIndexToken(uint token) => token & ~IndexSegmentTokenFlag;

    public static bool IsIndexToken(uint token) => (token & IndexSegmentTokenFlag) != 0;

    // ---- Instrument types ----------------------------------------------------------------------
    /// <summary>Dhan splits futures by underlying asset class (FUTSTK stock, FUTIDX index, FUTCOM
    /// commodity, FUTCUR currency). This app treats every futures contract identically, and its
    /// canonical instrument type is Kite's flat "FUT" - so all four normalize to that. Anything
    /// else (OPTSTK, EQUITY, ...) is not a future and is discarded by the instrument master.</summary>
    public static bool IsFuture(string? dhanInstrumentName) => dhanInstrumentName?.ToUpperInvariant() is
        "FUTSTK" or "FUTIDX" or "FUTCOM" or "FUTCUR" or "FUTIRC" or "FUTIRT";
}
