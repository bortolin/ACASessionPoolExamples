using System.Net.Http.Json;

namespace DemoAIDynamicSession.Web.CodeGeneration;

/// <summary>Client HTTP verso l'endpoint di generazione (senza esecuzione) di codice Python.</summary>
public sealed class PythonCodeGenerationApiClient(HttpClient httpClient)
{
    public async Task<PythonCodeGenerationResult> GenerateAsync(
        string prompt,
        IReadOnlyList<AttachedFileHint>? availableFiles = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/codegen/python",
            new PythonCodeGenerationRequest(prompt, availableFiles),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new InvalidOperationException(problem);
        }

        return await response.Content.ReadFromJsonAsync<PythonCodeGenerationResult>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito un risultato di generazione codice.");
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

        return $"La API di generazione codice ha risposto con stato {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }
}

public sealed record PythonCodeGenerationRequest(string Prompt, IReadOnlyList<AttachedFileHint>? AvailableFiles = null);

/// <summary>Nome e anteprima testuale (facoltativa) di un file già presente nella sessione ACA.</summary>
public sealed record AttachedFileHint(string FileName, string? Preview);

public sealed record PythonCodeGenerationResult(
    bool Success,
    string? Code,
    IReadOnlyList<string> ExpectedFiles,
    string? Notes,
    string? Error,
    string RawResponse);
