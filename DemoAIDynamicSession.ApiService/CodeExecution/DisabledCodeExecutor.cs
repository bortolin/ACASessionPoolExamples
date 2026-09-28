namespace DemoAIDynamicSession.ApiService.CodeExecution;

/// <summary>
/// Usato quando non è configurato alcun session pool e il fallback locale non è consentito.
/// </summary>
public sealed class DisabledCodeExecutor : ICodeExecutor
{
    public string Mode => CodeExecutorModes.Disabled;

    public Task<CodeExecutionResponse> ExecuteAsync(
        string code,
        string sessionId,
        CancellationToken cancellationToken) =>
        throw new CodeExecutionException(
            "Esecuzione codice non disponibile: configura 'DynamicSessions:PoolManagementEndpoint' con l'endpoint " +
            "di management del session pool di Azure Container Apps.");

    public Task<string> UploadFileAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken) =>
        throw new CodeExecutionException(
            "Caricamento file non disponibile: configura 'DynamicSessions:PoolManagementEndpoint' con l'endpoint " +
            "di management del session pool di Azure Container Apps.");

    public Task<IReadOnlyList<SessionFile>> ListFilesAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        throw new CodeExecutionException(
            "Elenco file non disponibile: configura 'DynamicSessions:PoolManagementEndpoint' con l'endpoint " +
            "di management del session pool di Azure Container Apps.");

    public Task<SessionFileContent> DownloadFileAsync(
        string sessionId,
        string fileName,
        CancellationToken cancellationToken) =>
        throw new CodeExecutionException(
            "Download file non disponibile: configura 'DynamicSessions:PoolManagementEndpoint' con l'endpoint " +
            "di management del session pool di Azure Container Apps.");
}
