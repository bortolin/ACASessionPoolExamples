using System.Net.Http.Json;

namespace DemoAIDynamicSession.Web.SemanticKernelDemo;

public sealed class SemanticKernelDemoApiClient(HttpClient httpClient)
{
    public async Task<PdfToExcelResult> ProcessAsync(
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");

        using var form = new MultipartFormDataContent
        {
            { fileContent, "file", fileName }
        };

        using var response = await httpClient.PostAsync(
            "/api/semantic-kernel-demo/pdf-to-excel",
            form,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SemanticKernelDemoException(await ReadProblemAsync(response, cancellationToken));
        }

        return await response.Content.ReadFromJsonAsync<PdfToExcelResult>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito il risultato dell'elaborazione.");
    }

    public async Task<(byte[] Content, string ContentType, string FileName)> DownloadAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/semantic-kernel-demo/files/{Uri.EscapeDataString(operationId)}",
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new SemanticKernelDemoException(await ReadProblemAsync(response, cancellationToken));
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString()
            ?? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "contatti.xlsx";

        return (content, contentType, fileName);
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
        }

        return $"La API ha risposto con stato {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }
}

public sealed class SemanticKernelDemoException(string message) : Exception(message);
