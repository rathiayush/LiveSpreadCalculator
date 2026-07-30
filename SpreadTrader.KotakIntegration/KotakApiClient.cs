using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpreadTrader.KotakIntegration.Auth;

namespace SpreadTrader.KotakIntegration;

/// <summary>One parsed response from a Kotak REST call. Owns the underlying JsonDocument, so
/// callers must dispose it (all of them use <c>using</c>) - same shape as DhanApiResponse.</summary>
public sealed class KotakApiResponse : IDisposable
{
    private readonly JsonDocument? _document;

    internal KotakApiResponse(bool success, HttpStatusCode statusCode, JsonDocument? document, string? errorMessage)
    {
        Success = success;
        StatusCode = statusCode;
        _document = document;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }
    public HttpStatusCode StatusCode { get; }
    public string? ErrorMessage { get; }

    /// <summary>The response body's top-level "data" element when present (the shape every Kotak
    /// endpoint researched for this integration returns - <c>{"data": {...}}</c> or
    /// <c>{"data": [...]}</c>), else the raw root. Only meaningful when <see cref="Success"/> is
    /// true; throws otherwise, same contract as DhanApiResponse.Root.</summary>
    public JsonElement Data
    {
        get
        {
            var root = _document?.RootElement
                ?? throw new InvalidOperationException("This Kotak response has no body to read - check Success first.");
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
        }
    }

    public bool HasBody => _document is not null;

    public void Dispose() => _document?.Dispose();
}

/// <summary>
/// The single place every Kotak Neo REST call goes through - the Kotak counterpart of DhanApiClient,
/// adapted for two things Kotak does differently:
///
///  1. TWO DIFFERENT AUTH HEADER SCHEMES, not one. The session-authenticated trading endpoints
///     (orders, positions, margin, limits) send <c>Sid</c>/<c>Auth</c> headers carrying
///     KotakSession's edit_sid/edit_token; a few others (scrip master, quotes) send a bare
///     <c>Authorization: {consumerKey}</c> header instead with no session token at all. See
///     <see cref="SendSessionAsync"/> vs <see cref="SendConsumerKeyAsync"/> - reverse-engineered
///     from the Kotak-Neo/Kotak-neo-api-v2 Python SDK's per-endpoint header_params, not verified
///     against a live account (see KotakOptions' doc comment).
///
///  2. FORM-URLENCODED BODIES, not JSON. Every order/margin/scrip-master call in the reference SDK
///     sends <c>Content-Type: application/x-www-form-urlencoded</c> with abbreviated field names
///     (see KotakSymbology's order-field comment on KotakOrderGateway) rather than a JSON object.
///
/// Error parsing is defensive/best-effort (try several plausible field names, fall back to the raw
/// body) rather than keyed to one confirmed shape, for the same reason KotakOptions' base URLs are
/// configurable: this integration has not yet been exercised against a real Kotak Neo account, so
/// the exact error envelope is unconfirmed. Tighten this once real responses are seen.
/// </summary>
public sealed class KotakApiClient
{
    private readonly HttpClient _httpClient;
    private readonly KotakOptions _options;
    private readonly KotakSession _session;
    private readonly KotakAuthFailureHandler _authFailureHandler;
    private readonly ILogger<KotakApiClient> _logger;

    public KotakApiClient(
        HttpClient httpClient,
        IOptions<KotakOptions> options,
        KotakSession session,
        KotakAuthFailureHandler authFailureHandler,
        ILogger<KotakApiClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _session = session;
        _authFailureHandler = authFailureHandler;
        _logger = logger;
    }

    /// <summary>True when there is a session (Sid+Auth) to send. Callers check this first so they
    /// can return a clear "complete the login flow" message instead of firing a request guaranteed
    /// to fail.</summary>
    public bool HasSession => _session.IsAuthenticated;

    public string? ClientId => _session.ClientId;

    /// <summary>Sends a request authenticated with the live session's Sid/Auth headers - every
    /// trading call (orders, positions, margin, limits) goes through this.
    /// <paramref name="formFields"/> is serialized as <c>application/x-www-form-urlencoded</c>;
    /// pass null for a GET/DELETE with no body.</summary>
    public Task<KotakApiResponse> SendSessionAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? formFields,
        string context,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>
        {
            ["Sid"] = _session.Sid ?? "",
            ["Auth"] = _session.AccessToken ?? "",
        };
        return SendAsync(method, path, formFields, headers, context, cancellationToken);
    }

    /// <summary>Sends a request authenticated with just the configured consumer key - the scrip
    /// master and quotes endpoints, per the reference SDK, need no session token at all.</summary>
    public Task<KotakApiResponse> SendConsumerKeyAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? formFields,
        string context,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string> { ["Authorization"] = _options.ConsumerKey };
        return SendAsync(method, path, formFields, headers, context, cancellationToken);
    }

    private async Task<KotakApiResponse> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyDictionary<string, string>? formFields,
        IReadOnlyDictionary<string, string> headers,
        string context,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{_options.RestBaseUrl.TrimEnd('/')}/{path.TrimStart('/')}");
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        if (formFields is { Count: > 0 })
            request.Content = new FormUrlEncodedContent(formFields);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return await ParseAsync(response.StatusCode, body, context, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Kotak request failed while {Context} ({Method} {Path})", context, method, path);
            return new KotakApiResponse(false, HttpStatusCode.ServiceUnavailable, null, ex.Message);
        }
    }

    private async Task<KotakApiResponse> ParseAsync(
        HttpStatusCode statusCode, string body, string context, CancellationToken cancellationToken)
    {
        JsonDocument? document = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { document = JsonDocument.Parse(body); }
            catch (JsonException) { document = null; }
        }

        var isSuccess = (int)statusCode is >= 200 and <= 299;

        // Even a 200 can carry a Kotak-side rejection in the body rather than the HTTP status -
        // "stat"/"status" "Not_Ok"/"error" fields have been observed in this family of Kotak APIs.
        // Treat the call as failed if the body says so, regardless of the HTTP status code.
        var bodySaysFailed = document is not null && BodyIndicatesFailure(document.RootElement);

        if (isSuccess && document is not null && !bodySaysFailed)
            return new KotakApiResponse(true, statusCode, document, null);

        var errorMessage = ReadError(document, body);
        document?.Dispose();

        await _authFailureHandler.HandleIfAuthFailureAsync(statusCode, errorMessage, context, cancellationToken);

        return new KotakApiResponse(false, statusCode, null,
            errorMessage ?? (isSuccess ? "Kotak returned an empty or unparseable body." : $"Kotak returned HTTP {(int)statusCode}."));
    }

    private static bool BodyIndicatesFailure(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("stat", out var stat) && stat.ValueKind == JsonValueKind.String &&
            !string.Equals(stat.GetString(), "Ok", StringComparison.OrdinalIgnoreCase))
            return true;

        return root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
               string.Equals(status.GetString(), "error", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadError(JsonDocument? document, string rawBody)
    {
        if (document is null)
            return string.IsNullOrWhiteSpace(rawBody) ? null : Truncate(rawBody);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return Truncate(rawBody);

        // Tried in order of plausibility - none of these are confirmed against a real Kotak error
        // response yet (see the class doc comment), so several candidates are tried rather than
        // committing to one and silently reporting "no message" for the others.
        foreach (var field in new[] { "errMsg", "emsg", "message", "Message", "error", "errorMessage" })
        {
            if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }

        return Truncate(rawBody);
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500] + "...";
}
