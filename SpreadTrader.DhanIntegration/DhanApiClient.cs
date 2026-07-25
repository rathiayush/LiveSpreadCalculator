using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.DhanIntegration.Auth;

namespace SpreadTrader.DhanIntegration;

/// <summary>
/// One parsed response from a Dhan REST call. Owns the underlying JsonDocument, so callers must
/// dispose it (all of them use <c>using</c>).
/// </summary>
public sealed class DhanApiResponse : IDisposable
{
    private readonly JsonDocument? _document;

    internal DhanApiResponse(
        bool success,
        HttpStatusCode statusCode,
        JsonDocument? document,
        string? errorCode,
        string? errorMessage)
    {
        Success = success;
        StatusCode = statusCode;
        _document = document;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    /// <summary>True for a 2xx response that parsed cleanly. Dhan signals failure with the HTTP
    /// status code plus an error body - there is no "status":"success" envelope like Kite's.</summary>
    public bool Success { get; }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Dhan's machine-readable error code, e.g. "DH-901" (invalid authentication) or
    /// "DH-906" (order error). Null on success or when the error body had no code.</summary>
    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    /// <summary>The parsed response body. Only meaningful when <see cref="Success"/> is true;
    /// throws otherwise, which is intentional - a caller that reads data off a failed response has
    /// a bug.</summary>
    public JsonElement Root => _document?.RootElement
        ?? throw new InvalidOperationException("This Dhan response has no body to read - check Success first.");

    public bool HasBody => _document is not null;

    public void Dispose() => _document?.Dispose();
}

/// <summary>
/// The single place every Dhan REST call goes through. It exists so that the four adapters
/// (orders, margin, positions, instruments) don't each repeat - and each get subtly wrong - the
/// four things every Dhan v2 request needs:
///
///  1. Auth headers. Dhan authenticates with two headers, <c>access-token</c> and <c>client-id</c>,
///     not an Authorization header. Omitting client-id fails in ways that look like a bad token.
///  2. <c>dhanClientId</c> injected into every JSON body. Dhan requires it in the payload as well
///     as the header on order and position endpoints, and rejects the request if it disagrees with
///     the header. Injecting it centrally means no call site can forget it or set it inconsistently.
///  3. Error parsing. Failures come back as an HTTP status plus
///     <c>{ "errorType", "errorCode", "errorMessage" }</c>, which is nothing like Kite's
///     <c>{"status":"error"}</c> envelope.
///  4. Auth-failure detection, routed to DhanAuthFailureHandler on every non-success response so a
///     dead token clears the session exactly once, wherever it is first noticed.
/// </summary>
public sealed class DhanApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly DhanOptions _options;
    private readonly DhanSession _session;
    private readonly DhanAuthFailureHandler _authFailureHandler;
    private readonly ILogger<DhanApiClient> _logger;

    public DhanApiClient(
        HttpClient httpClient,
        IOptions<DhanOptions> options,
        DhanSession session,
        DhanAuthFailureHandler authFailureHandler,
        ILogger<DhanApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _authFailureHandler = authFailureHandler;
        _logger = logger;
    }

    /// <summary>True when there is a token and a client id to send. Callers check this first so
    /// they can return a clear "complete the login flow" message instead of firing a request that
    /// is guaranteed to 401.</summary>
    public bool HasSession => _session.IsAuthenticated;

    public string? ClientId => _session.ClientId;

    /// <summary>Sends an authenticated request and parses the outcome. <paramref name="payload"/>
    /// is serialized as JSON with <c>dhanClientId</c> added; pass null for GET/DELETE.
    /// <paramref name="context"/> is a short human phrase ("placing order for X") used in auth
    /// failure logging.
    ///
    /// Never throws for a transport failure - a network error comes back as an unsuccessful
    /// response with the exception message, because every caller here is on a trading hot path
    /// where a thrown exception would be caught and flattened into exactly that anyway.</summary>
    public async Task<DhanApiResponse> SendAsync(
        HttpMethod method,
        string path,
        object? payload,
        string context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{_options.RestBaseUrl}{path}");
        request.Headers.TryAddWithoutValidation("access-token", _session.AccessToken ?? "");
        request.Headers.TryAddWithoutValidation("client-id", _session.ClientId ?? "");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (payload is not null)
            request.Content = new StringContent(SerializeWithClientId(payload), Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return await ParseAsync(response.StatusCode, body, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Dhan request failed while {Context} ({Method} {Path})", context, method, path);
            return new DhanApiResponse(false, HttpStatusCode.ServiceUnavailable, null, null, ex.Message);
        }
    }

    /// <summary>Serializes the payload and splices in <c>dhanClientId</c>. Done on the serialized
    /// JSON rather than by requiring every payload type to carry the field, so call sites can keep
    /// using terse anonymous objects that describe only the order/position they care about.</summary>
    private string SerializeWithClientId(object payload)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var clientId = _session.ClientId;
        if (string.IsNullOrEmpty(clientId)) return json;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return json; // an array payload (none today) has nowhere to put it - send as-is

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("dhanClientId", clientId);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("dhanClientId")) continue; // never emit it twice
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<DhanApiResponse> ParseAsync(
        HttpStatusCode statusCode,
        string body,
        string context,
        CancellationToken cancellationToken)
    {
        JsonDocument? document = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { document = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                // Dhan occasionally answers with a bare HTML error page from an edge proxy rather
                // than JSON. Treat it as a failure carrying the raw text, not a crash.
                document = null;
            }
        }

        var isSuccess = (int)statusCode is >= 200 and <= 299;
        if (isSuccess && document is not null)
            return new DhanApiResponse(true, statusCode, document, null, null);

        var (errorCode, errorMessage) = ReadError(document, body);
        document?.Dispose();

        await _authFailureHandler.HandleIfAuthFailureAsync(statusCode, errorCode, context, cancellationToken);

        return new DhanApiResponse(false, statusCode, null, errorCode,
            errorMessage ?? (isSuccess ? "Dhan returned an empty or unparseable body." : $"Dhan returned HTTP {(int)statusCode}."));
    }

    /// <summary>Pulls errorCode/errorMessage out of a Dhan error body. Handles both the documented
    /// object shape and the single-element array some endpoints return, and falls back to the raw
    /// body so nothing is ever reported as an empty error.</summary>
    private static (string? Code, string? Message) ReadError(JsonDocument? document, string rawBody)
    {
        if (document is null)
            return (null, string.IsNullOrWhiteSpace(rawBody) ? null : Truncate(rawBody));

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            root = root[0];

        if (root.ValueKind != JsonValueKind.Object)
            return (null, Truncate(rawBody));

        var code = root.TryGetProperty("errorCode", out var c) ? c.GetString() : null;
        var message = root.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
        var type = root.TryGetProperty("errorType", out var t) ? t.GetString() : null;

        // Some endpoints (notably order rejection) put the exchange's own text in omsErrorDescription.
        message ??= root.TryGetProperty("omsErrorDescription", out var oms) ? oms.GetString() : null;

        var combined = (type, message) switch
        {
            (not null, not null) => $"{type}: {message}",
            (not null, null) => type,
            (null, not null) => message,
            _ => Truncate(rawBody),
        };

        return (code, combined);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
