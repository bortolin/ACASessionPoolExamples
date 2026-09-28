using System.Net;
using System.Net.Http.Json;
using DemoAIDynamicSession.Web.CodeExecution;

namespace DemoAIDynamicSession.Web.SessionManagement;

public sealed class SessionManagementApiClient(HttpClient httpClient)
{
    public async Task<SessionManagementStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await httpClient.GetFromJsonAsync<SessionManagementStatus>("/api/sessions/status", cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito lo stato della gestione sessioni.");

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/sessions", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SessionInfo[]>(cancellationToken) ?? [];
    }

    /// <summary>Restituisce <c>null</c> se la sessione non esiste più.</summary>
    public async Task<SessionDetails?> GetSessionAsync(
        string identifier,
        bool includeFiles,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/sessions/{Uri.EscapeDataString(identifier)}?includeFiles={(includeFiles ? "true" : "false")}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SessionDetails>(cancellationToken);
    }

    /// <summary>Restituisce <c>false</c> se la sessione era già terminata.</summary>
    public async Task<bool> DeleteSessionAsync(string identifier, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync(
            $"/api/sessions/{Uri.EscapeDataString(identifier)}",
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return true;
    }

    public async Task<SessionInfo> CreateSessionAsync(string? identifier, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/sessions",
            new { identifier },
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SessionInfo>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito la sessione creata.");
    }

    public async Task<SessionInfo> KeepAliveAsync(string identifier, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync(
            $"/api/sessions/{Uri.EscapeDataString(identifier)}/keep-alive",
            content: null,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<SessionInfo>(cancellationToken)
            ?? throw new InvalidOperationException("La API non ha restituito la sessione aggiornata.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? message = null;
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(cancellationToken);
            message = !string.IsNullOrWhiteSpace(problem?.Detail) ? problem.Detail : problem?.Title;
        }
        catch (Exception)
        {
            // Risposta non conforme a ProblemDetails: si usa il messaggio generico.
        }

        throw new CodeExecutionFailedException(
            string.IsNullOrWhiteSpace(message)
                ? $"La API ha risposto con stato {(int)response.StatusCode} ({response.ReasonPhrase})."
                : message);
    }
}

public sealed record SessionInfo(
    string Identifier,
    string? Etag,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastAccessedAt);

public sealed record SessionDetails(SessionInfo Session, IReadOnlyList<SessionFile>? Files);

public sealed record SessionManagementStatus(
    bool IsAvailable,
    string? PoolManagementEndpoint,
    string? PoolName,
    string? Region,
    string ManagementApiVersion,
    string Description);
