namespace DemoAIDynamicSession.ApiService.CodeExecution;

public sealed class DynamicSessionsOptions
{
    public const string SectionName = "DynamicSessions";

    /// <summary>
    /// Endpoint di management del session pool, ad esempio
    /// </summary>
    public string? PoolManagementEndpoint { get; set; }

    public string ApiVersion { get; set; } = "2025-10-02-preview";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Consente l'esecuzione locale non isolata quando il pool Azure non è configurato.
    /// Attivo solo in ambiente di sviluppo.
    /// </summary>
    public bool AllowLocalFallback { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PoolManagementEndpoint);
}
