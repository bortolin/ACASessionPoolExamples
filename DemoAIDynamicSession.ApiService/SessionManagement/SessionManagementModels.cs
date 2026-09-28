using DemoAIDynamicSession.ApiService.CodeExecution;

namespace DemoAIDynamicSession.ApiService.SessionManagement;

public sealed class SessionManagementOptions
{
    /// <summary>
    /// Riutilizza la stessa sezione di configurazione del code executor
    /// (<c>DynamicSessions:PoolManagementEndpoint</c>).
    /// </summary>
    public string? PoolManagementEndpoint { get; set; }

    /// <summary>
    /// Le API di gestione sessioni (getSession, listSessions, delete) sono documentate
    /// con una api-version dedicata, diversa da quella usata per executions/files.
    /// </summary>
    public string ManagementApiVersion { get; set; } = "2025-02-02-preview";

    /// <summary>Numero massimo di pagine lette da listSessions (300 sessioni per pagina).</summary>
    public int MaxListPages { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PoolManagementEndpoint);
}

/// <summary>Rappresentazione di una sessione restituita dal pool (SessionView).</summary>
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

public sealed record CreateSessionRequest(string? Identifier);
