using System.Net.Http.Json;

namespace DemoAIDynamicSession.Web.CodeExecution;

public sealed class CodeExecutionApiClient(HttpClient httpClient)
{
    public async Task<CodeExecutionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<CodeExecutionStatus>("/api/code/status", cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito lo stato dell'esecutore.");

    public async Task<IReadOnlyList<CodeSample>> GetSamplesAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<CodeSample[]>("/api/code/samples", cancellationToken) ?? [];

    public async Task<CodeExecutionResult> ExecuteAsync(
        CodeExecutionInput input,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/code/execute", input, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new CodeExecutionFailedException(problem);
        }

        return await response.Content.ReadFromJsonAsync<CodeExecutionResult>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito il risultato dell'esecuzione.");
    }

    public async Task<IReadOnlyList<SessionFile>> GetFilesAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<SessionFile[]>(
            $"/api/code/files?sessionId={Uri.EscapeDataString(sessionId)}",
            cancellationToken) ?? [];

    public async Task<(byte[] Content, string ContentType)> DownloadFileAsync(
        string sessionId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/code/files/{Uri.EscapeDataString(fileName)}/content?sessionId={Uri.EscapeDataString(sessionId)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new CodeExecutionFailedException(problem);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        return (bytes, contentType);
    }

    public async Task<FileUploadResult> UploadFileAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            "application/octet-stream");

        using var form = new MultipartFormDataContent
        {
            { fileContent, "file", fileName }
        };

        using var response = await httpClient.PostAsync(
            $"/api/code/files?sessionId={Uri.EscapeDataString(sessionId)}",
            form,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new CodeExecutionFailedException(problem);
        }

        return await response.Content.ReadFromJsonAsync<FileUploadResult>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito il risultato del caricamento.");
    }

    private static async Task<string> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(cancellationToken);

            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem?.Title))
            {
                return problem.Title;
            }
        }
        catch (Exception)
        {
            // Risposta non conforme a ProblemDetails: si usa il messaggio generico.
        }

        return $"La API ha risposto con stato {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }
}

public sealed class CodeExecutionFailedException(string message) : Exception(message);
