using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using DemoAIDynamicSession.ApiService.CodeExecution;
using Microsoft.Extensions.Options;

namespace DemoAIDynamicSession.ApiService.SessionManagement;

/// <summary>
/// Client per le API di gestione del session pool (lista, dettaglio ed eliminazione delle sessioni).
/// https://learn.microsoft.com/azure/container-apps/sessions-code-interpreter#retrieve-session-information
/// </summary>
public sealed class DynamicSessionsManagementClient(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<SessionManagementOptions> options,
    ILogger<DynamicSessionsManagementClient> logger)
{
    private static readonly string[] Scopes = ["https://dynamicsessions.io/.default"];
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static AccessToken _token;

    private readonly SessionManagementOptions _options = options.Value;

    // Costruzione dell'URI per le operazioni sul session pool
    // https://<pool-name>.dynamicsessions.io/<operation>?api-version=<api-version>&identifier=<session-id>
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken)
    {
        var sessions = new List<SessionInfo>();
        var skip = 0;

        for (var page = 0; page < Math.Max(1, _options.MaxListPages); page++)
        {
            var (items, hasNext) = await ListPageAsync(skip, cancellationToken);
            sessions.AddRange(items);

            if (!hasNext || items.Count == 0)
            {
                break;
            }

            skip += items.Count;
        }

        return sessions
            .OrderByDescending(session => session.LastAccessedAt ?? session.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    // Recupera le informazioni di una singola sessione tramite il suo identificatore.
    // GET https://<pool-name>.dynamicsessions.io/session?api-version=<api-version>&identifier=<session-id>
    public async Task<SessionInfo?> GetSessionAsync(string identifier, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "session", identifier, skip: null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var payload = await EnsureSuccessAsync(response, "lettura della sessione", cancellationToken);
        using var document = JsonDocument.Parse(payload);
        return ParseSession(document.RootElement);
    }

    /// <summary>Termina la sessione. Restituisce <c>false</c> se la sessione non esiste.</summary>
    /// DELETE https://<pool-name>.dynamicsessions.io/session?api-version=<api-version>&identifier=<session-id>
    public async Task<bool> DeleteSessionAsync(string identifier, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, "session", identifier, skip: null, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, "eliminazione della sessione", cancellationToken);
        return true;
    }

    // Recupera una pagina di sessioni dal session pool.
    // GET https://<pool-name>.dynamicsessions.io/listSessions?api-version=<api-version>&skip=<skip>
    private async Task<(IReadOnlyList<SessionInfo> Items, bool HasNext)> ListPageAsync(
        int skip,
        CancellationToken cancellationToken)
    {
        // La documentazione riporta sia "/listSessions" sia "/.management/listSessions":
        // si prova il primo e, se il pool risponde 404, si ripiega sul secondo.
        var response = await SendAsync(HttpMethod.Get, "listSessions", identifier: null, skip, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            response = await SendAsync(HttpMethod.Get, ".management/listSessions", identifier: null, skip, cancellationToken);
        }

        using (response)
        {
            var payload = await EnsureSuccessAsync(response, "lettura delle sessioni", cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            // La documentazione descrive "value", ma il pool restituisce "sessions": si accettano entrambi.
            var array = root.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array
                ? sessions
                : root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
                    ? value
                    : default;

            var items = array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(ParseSession).OfType<SessionInfo>().ToArray()
                : [];

            var hasNext = root.TryGetProperty("nextLink", out var nextLink) &&
                          nextLink.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(nextLink.GetString());

            return (items, hasNext);
        }
    }

    // Invia una richiesta HTTP al session pool, gestendo l'autenticazione e la costruzione dell'URI.
    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? identifier,
        int? skip,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.PoolManagementEndpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("Il pool management endpoint non è configurato.");

        var query = $"api-version={Uri.EscapeDataString(_options.ManagementApiVersion)}";
        if (identifier is not null)
        {
            query += $"&identifier={Uri.EscapeDataString(identifier)}";
        }

        if (skip is not null)
        {
            query += $"&skip={skip.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        using var request = new HttpRequestMessage(method, $"{endpoint}/{path}?{query}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(cancellationToken));

        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<string> EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return payload;
        }

        logger.LogError(
            "Session pool: {Operation} fallita ({StatusCode}): {Payload}",
            operation,
            (int)response.StatusCode,
            payload);

        throw new CodeExecutionException(
            $"Il session pool ha restituito {(int)response.StatusCode} ({response.ReasonPhrase}) durante la {operation}. " +
            (payload.Length <= 800 ? payload : payload[..800] + "…"));
    }

    // Analizza un elemento JSON e restituisce le informazioni sulla sessione.
    private static SessionInfo? ParseSession(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Alcune api-version incapsulano i dati in "properties".
        var properties = item.TryGetProperty("properties", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : item;

        var identifier = GetString(properties, "identifier") ?? GetString(item, "identifier");
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        return new SessionInfo(
            identifier,
            GetString(properties, "etag") ?? GetString(item, "etag"),
            GetDate(properties, "expiresAt") ?? GetDate(properties, "expireAt"),
            GetDate(properties, "createdAt"),
            GetDate(properties, "lastAccessedAt"));
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? GetDate(JsonElement element, string propertyName) =>
        DateTimeOffset.TryParse(
            GetString(element, propertyName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _token.Token;
        }

        await TokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                _token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
            }

            return _token.Token;
        }
        finally
        {
            TokenLock.Release();
        }
    }
}
