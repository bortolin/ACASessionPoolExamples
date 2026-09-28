using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace DemoAIDynamicSession.ApiService.CodeExecution;

/// <summary>
/// Esegue codice tramite la management API di un session pool "code interpreter"
/// di Azure Container Apps dynamic sessions.
/// </summary>
public sealed class DynamicSessionsCodeExecutor(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<DynamicSessionsOptions> options,
    ILogger<DynamicSessionsCodeExecutor> logger) : ICodeExecutor
{
    private static readonly string[] Scopes = ["https://dynamicsessions.io/.default"];

    private readonly DynamicSessionsOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AccessToken _token;

    public string Mode => CodeExecutorModes.AzureDynamicSessions;

    public async Task<CodeExecutionResponse> ExecuteAsync(
        string code,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.PoolManagementEndpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("Il pool management endpoint non è configurato.");

        // Costruzione dell'URI per l'esecuzione del codice sul session pool
        // https://<pool-name>.dynamicsessions.io/executions?api-version=<api-version>&identifier=<session-id>
        var requestUri = $"{endpoint}/executions?api-version={Uri.EscapeDataString(_options.ApiVersion)}&identifier={Uri.EscapeDataString(sessionId)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            // Corpo della richiesta JSON contenente il codice da eseguire
            // codice in linea e esecuzione sincrona
            Content = JsonContent.Create(new
            {
                codeInputType = "inline",
                executionType = "synchronous",
                code,
                timeoutInSeconds = _options.TimeoutSeconds
            })
        };

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(cancellationToken));

        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.SendAsync(request, cancellationToken);
        stopwatch.Stop();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Esecuzione su dynamic sessions fallita ({StatusCode}): {Payload}",
                (int)response.StatusCode,
                payload);

            throw new CodeExecutionException(
                $"Il session pool ha restituito {(int)response.StatusCode} ({response.ReasonPhrase}). {Truncate(payload, 800)}");
        }

        return Parse(payload, sessionId, stopwatch.Elapsed.TotalMilliseconds, Mode);
    }

    // Caricamento di un file nel session pool
    public async Task<string> UploadFileAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.PoolManagementEndpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("Il pool management endpoint non è configurato.");

        var requestUri = $"{endpoint}/files?api-version={Uri.EscapeDataString(_options.ApiVersion)}&identifier={Uri.EscapeDataString(sessionId)}";

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        using var fileContent = new ByteArrayContent(buffer.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var form = new MultipartFormDataContent
        {
            { fileContent, "file", fileName }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(cancellationToken));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Caricamento file su dynamic sessions fallito ({StatusCode}): {Payload}",
                (int)response.StatusCode,
                payload);

            throw new CodeExecutionException(
                $"Il session pool ha restituito {(int)response.StatusCode} ({response.ReasonPhrase}) durante il caricamento del file. {Truncate(payload, 800)}");
        }

        // I file caricati nelle dynamic sessions Azure sono sempre disponibili in /mnt/data.
        return $"/mnt/data/{fileName}";
    }

    public async Task<IReadOnlyList<SessionFile>> ListFilesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.PoolManagementEndpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("Il pool management endpoint non è configurato.");

        var requestUri = $"{endpoint}/files?api-version={Uri.EscapeDataString(_options.ApiVersion)}&identifier={Uri.EscapeDataString(sessionId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(cancellationToken));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Lettura file da dynamic sessions fallita ({StatusCode}): {Payload}",
                (int)response.StatusCode,
                payload);

            throw new CodeExecutionException(
                $"Il session pool ha restituito {(int)response.StatusCode} ({response.ReasonPhrase}) durante la lettura dei file. {Truncate(payload, 800)}");
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Select(ParseSessionFile)
            .Where(file => file is not null)
            .Select(file => file!)
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<SessionFileContent> DownloadFileAsync(
        string sessionId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var endpoint = _options.PoolManagementEndpoint?.TrimEnd('/')
            ?? throw new InvalidOperationException("Il pool management endpoint non è configurato.");

        var requestUri = $"{endpoint}/files/{Uri.EscapeDataString(fileName)}/content" +
            $"?api-version={Uri.EscapeDataString(_options.ApiVersion)}&identifier={Uri.EscapeDataString(sessionId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await GetAccessTokenAsync(cancellationToken));

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Download file da dynamic sessions fallito ({StatusCode}): {Payload}",
                (int)response.StatusCode,
                payload);

            throw new CodeExecutionException(
                $"Il session pool ha restituito {(int)response.StatusCode} ({response.ReasonPhrase}) durante il download del file. {Truncate(payload, 800)}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString();

        return new SessionFileContent(
            bytes,
            string.IsNullOrWhiteSpace(contentType) ? ContentTypeResolver.FromFileName(fileName) : contentType);
    }

    private static SessionFile? ParseSessionFile(JsonElement item)
    {
        var properties = item;
        if (item.TryGetProperty("properties", out var nestedProperties) &&
            nestedProperties.ValueKind == JsonValueKind.Object)
        {
            properties = nestedProperties;
        }

        var fileName = GetString(properties, "filename") ?? GetString(properties, "name");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var size = GetInt64(properties, "size") ?? GetInt64(properties, "sizeInBytes") ?? 0;

        var lastModifiedTime = GetString(properties, "lastModifiedTime") ??
                               GetString(properties, "lastModifiedAt");
        var parsedLastModifiedTime = DateTimeOffset.TryParse(
            lastModifiedTime,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var timestamp)
            ? timestamp
            : DateTimeOffset.MinValue;

        return new SessionFile(fileName, size, parsedLastModifiedTime);
    }

    private static long? GetInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt64(out var value)
            ? value
            : null;

    internal static CodeExecutionResponse Parse(string payload, string sessionId, double fallbackDurationMs, string mode)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        // Alcune api-version incapsulano il risultato in "properties".
        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            root = properties;
        }

        var status = GetString(root, "status") ?? "Succeeded";

        var result = root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Object
            ? resultElement
            : root;

        var stdout = GetString(result, "stdout") ?? string.Empty;
        var stderr = GetString(result, "stderr") ?? string.Empty;
        var executionResult = GetValue(result, "executionResult");

        var durationMs = result.TryGetProperty("executionTimeInMilliseconds", out var durationElement)
            && durationElement.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : fallbackDurationMs;

        return new CodeExecutionResponse(
            sessionId,
            status,
            stdout,
            stderr,
            executionResult,
            durationMs,
            mode,
            DateTimeOffset.UtcNow);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _token.Token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
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
            _tokenLock.Release();
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            _ => value.GetRawText()
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

public sealed class CodeExecutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public static class CodeExecutorModes
{
    public const string AzureDynamicSessions = "azure-dynamic-sessions";
    public const string LocalFallback = "local-fallback";
    public const string Disabled = "disabled";
}
