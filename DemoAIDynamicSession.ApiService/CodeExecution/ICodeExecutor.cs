namespace DemoAIDynamicSession.ApiService.CodeExecution;

public interface ICodeExecutor
{
    /// <summary>Identifica la modalità attiva: "azure-dynamic-sessions" oppure "local-fallback".</summary>
    string Mode { get; }

    Task<CodeExecutionResponse> ExecuteAsync(string code, string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Carica un file nel filesystem della sessione (per le dynamic sessions Azure finisce in
    /// <c>/mnt/data/{fileName}</c>). Restituisce il percorso assoluto utilizzabile dal codice generato.
    /// </summary>
    Task<string> UploadFileAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionFile>> ListFilesAsync(
        string sessionId,
        CancellationToken cancellationToken);

    /// <summary>Scarica il contenuto di un file prodotto nella sessione.</summary>
    Task<SessionFileContent> DownloadFileAsync(
        string sessionId,
        string fileName,
        CancellationToken cancellationToken);
}
