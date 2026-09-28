using System.ComponentModel.DataAnnotations;

namespace DemoAIDynamicSession.ApiService.CodeExecution;

public sealed class CodeExecutionRequest
{
    [Required(ErrorMessage = "Il codice da eseguire è obbligatorio.")]
    [StringLength(20000, ErrorMessage = "Il codice non può superare 20000 caratteri.")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Identificatore logico della sessione: richieste con lo stesso identificatore
    /// vengono instradate verso la stessa sandbox, che ne mantiene lo stato.
    /// </summary>
    [StringLength(128, ErrorMessage = "L'identificatore di sessione non può superare 128 caratteri.")]
    public string? SessionId { get; set; }
}

public sealed record CodeExecutionResponse(
    string SessionId,
    string Status,
    string Stdout,
    string Stderr,
    string? Result,
    double DurationMs,
    string ExecutorMode,
    DateTimeOffset ExecutedAt);

public sealed record CodeExecutionStatus(
    string ExecutorMode,
    bool IsAzureConfigured,
    string? PoolManagementEndpoint,
    string ApiVersion,
    bool IsSandboxed,
    string Description);

public sealed record CodeSample(string Id, string Title, string Description, string Code);

public sealed record SessionFile(
    string FileName,
    long Size,
    DateTimeOffset LastModifiedTime);

public sealed record FileUploadResponse(string FileName, string Path);

/// <summary>Contenuto binario di un file di sessione, con il content type da restituire al client.</summary>
public sealed record SessionFileContent(byte[] Content, string ContentType);
