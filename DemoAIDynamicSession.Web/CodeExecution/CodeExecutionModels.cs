namespace DemoAIDynamicSession.Web.CodeExecution;

public sealed record CodeExecutionResult(
    string SessionId,
    string Status,
    string Stdout,
    string Stderr,
    string? Result,
    double DurationMs,
    string ExecutorMode,
    DateTimeOffset ExecutedAt)
{
    public bool IsSuccess => string.Equals(Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
}

public sealed record CodeExecutionStatus(
    string ExecutorMode,
    bool IsAzureConfigured,
    string? PoolManagementEndpoint,
    string ApiVersion,
    bool IsSandboxed,
    string Description);

public sealed record CodeSample(string Id, string Title, string Description, string Code);

public sealed record CodeExecutionInput(string Code, string? SessionId);

public sealed record SessionFile(
    string FileName,
    long Size,
    DateTimeOffset LastModifiedTime);

public sealed record FileUploadResult(string FileName, string Path);

public static class CodeExecutorModes
{
    public const string AzureDynamicSessions = "azure-dynamic-sessions";
    public const string LocalFallback = "local-fallback";
    public const string Disabled = "disabled";
}
