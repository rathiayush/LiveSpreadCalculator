namespace SpreadTrader.KotakIntegration;

/// <summary>
/// The single translation table between this app's canonical (Kite-flavoured) vocabulary and
/// Kotak Neo's - same role and same "keep every broker-specific mapping in one reviewable file" as
/// DhanSymbology, see its doc comment for the full rationale.
///
/// Reverse-engineered from the public Kotak-Neo/Kotak-neo-api-v2 Python SDK's
/// neo_api_client/req_data_validation.py and neo_api_client/api/order_api.py (this deployment has
/// no live Kotak Neo account yet to verify against directly - see KotakOptions' doc comment).
/// Unlike Dhan, several of Kotak's own enum spellings already coincide with the canonical
/// vocabulary (DAY/IOC validity, SL/SL-M order type, NRML/MIS product), so those map to themselves;
/// only the ones that genuinely differ (order type MARKET/LIMIT, exchange segment, transaction
/// type B/S) need real translation.
///
/// NO MCX MAPPING. Kotak Neo's documented exchange-segment values are NSE, BSE, NFO, BFO, CDS, BCD
/// - MCX does not appear anywhere in the SDK's validation list. <see cref="ToKotakSegment"/>
/// therefore has no MCX entry and falls back to returning the canonical code unchanged, which
/// Kotak's API will reject - this is a genuine, confirmed gap: an instance running the MCX add-on
/// (silver/gold/crude spreads) cannot be switched to Kotak until/unless Kotak's API is confirmed to
/// support it some other way.
/// </summary>
public static class KotakSymbology
{
    // ---- Exchange segments ---------------------------------------------------------------------
    // Canonical (Kite-style) exchange code -> Kotak exchange_segment string ("es" field on orders,
    // and the segment half of a WebSocket "exchange_segment|token" subscription entry).
    private static readonly Dictionary<string, string> CanonicalToSegment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NFO"] = "nse_fo",
        ["NSE"] = "nse_cm",
        ["BSE"] = "bse_cm",
        ["BFO"] = "bse_fo",
        ["CDS"] = "cde_fo",
        ["BCD"] = "bcs_fo",
        // Deliberately no "MCX" entry - see the class doc comment.
    };

    private static readonly Dictionary<string, string> SegmentToCanonical =
        CanonicalToSegment.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>"NFO" -&gt; "nse_fo". Falls back to the input unchanged (which Kotak will reject)
    /// for anything with no mapping - notably "MCX" - rather than throwing, so the caller's own
    /// error handling reports Kotak's rejection with context instead of an unrelated exception.</summary>
    public static string ToKotakSegment(string canonicalExchange) =>
        CanonicalToSegment.TryGetValue(canonicalExchange, out var segment) ? segment : canonicalExchange.ToLowerInvariant();

    public static string ToCanonicalExchange(string kotakSegment) =>
        SegmentToCanonical.TryGetValue(kotakSegment, out var exchange) ? exchange : kotakSegment.ToUpperInvariant();

    // ---- Products --------------------------------------------------------------------------------
    /// <summary>Canonical product -&gt; Kotak productType ("pc" field). Kotak's own valid values
    /// (NRML/CNC/MIS/CO/BO - see req_data_validation.py) already coincide with the canonical ones
    /// this app uses, so this is closer to a validation/passthrough than a real translation -
    /// unlike DhanSymbology's NRML-&gt;MARGIN, there is no lossy remapping to get wrong here.</summary>
    public static string ToKotakProduct(string canonicalProduct) => canonicalProduct.ToUpperInvariant() switch
    {
        "NRML" => "NRML",
        "MIS" => "MIS",
        "CNC" => "CNC",
        "CO" => "CO",
        "BO" => "BO",
        _ => canonicalProduct.ToUpperInvariant(),
    };

    public static string ToCanonicalProduct(string kotakProduct) => kotakProduct.ToUpperInvariant() switch
    {
        "INTRADAY" => "MIS", // Kotak's positions endpoint has been observed to use the long form
        _ => kotakProduct.ToUpperInvariant(),
    };

    // ---- Order types -------------------------------------------------------------------------------
    /// <summary>Canonical order type -&gt; Kotak's "pt" (price type) field. MARKET/LIMIT need real
    /// translation (Kotak spells them MKT/L); SL and SL-M already match Kotak's own spelling
    /// exactly, so those pass straight through.</summary>
    private static readonly Dictionary<string, string> CanonicalToOrderType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MARKET"] = "MKT",
        ["LIMIT"] = "L",
        ["SL"] = "SL",
        ["SL-M"] = "SL-M",
    };

    public static string ToKotakOrderType(string canonicalOrderType) =>
        CanonicalToOrderType.TryGetValue(canonicalOrderType, out var orderType) ? orderType : canonicalOrderType.ToUpperInvariant();

    // ---- Validity ----------------------------------------------------------------------------------
    /// <summary>Kotak's validity values (DAY, IOC) are spelled identically to the canonical ones
    /// this app already uses - a straight passthrough, kept as a named method (rather than sending
    /// leg.Validity directly) so every other translation point in this file stays the obvious place
    /// to look if that ever stops being true.</summary>
    public static string ToKotakValidity(string canonicalValidity) => canonicalValidity.ToUpperInvariant();

    // ---- Transaction type ----------------------------------------------------------------------
    /// <summary>Canonical BUY/SELL -&gt; Kotak's single-letter "tt" field.</summary>
    public static string ToKotakTransactionType(string canonicalTransactionType) =>
        string.Equals(canonicalTransactionType, "BUY", StringComparison.OrdinalIgnoreCase) ? "B" : "S";

    // ---- Order status --------------------------------------------------------------------------
    /// <summary>Kotak order-book status strings -&gt; the canonical (Kite-flavoured) status the
    /// engine already branches on. Reverse-engineered from common Kotak Xtreme/Neo status
    /// vocabulary (this app has no live account to confirm the exact spellings emitted by
    /// order_report/order_history yet - flagged in KotakOrderGateway's doc comment as something to
    /// verify against a real response before relying on this in production).</summary>
    public static string ToCanonicalOrderStatus(string? kotakStatus) => kotakStatus?.ToUpperInvariant() switch
    {
        "COMPLETE" or "TRADED" or "EXECUTED" => "COMPLETE",
        "REJECTED" => "REJECTED",
        "CANCELLED" or "CANCELED" => "CANCELLED",
        "OPEN" or "PENDING" or "TRIGGER PENDING" or "OPEN PENDING" or "PUT ORDER REQ RECEIVED" => "OPEN",
        null or "" => "OPEN",
        _ => kotakStatus!.ToUpperInvariant(),
    };

    public static bool IsTerminalStatus(string canonicalStatus) =>
        canonicalStatus is "COMPLETE" or "REJECTED" or "CANCELLED";
}
