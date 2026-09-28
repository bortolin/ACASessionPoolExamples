namespace DemoAIDynamicSession.ApiService.SemanticKernelDemo;

public sealed record ExtractedContact(
    string FirstName,
    string LastName,
    string Company,
    string Role,
    string Email,
    string Phone);

public sealed record ProcessingStep(
    int Order,
    string Title,
    string Description,
    string Status,
    string Component,
    string? Detail = null);

public sealed record PdfToExcelResponse(
    string OperationId,
    string SessionId,
    bool IsSimulated,
    string SourceFileName,
    string GeneratedFileName,
    int ContactCount,
    DateTimeOffset CompletedAt,
    string KernelResponse,
    IReadOnlyList<ExtractedContact> Contacts,
    IReadOnlyList<ProcessingStep> Steps);

internal sealed record GeneratedWorkbook(
    byte[] Content,
    string FileName,
    DateTimeOffset ExpiresAt);
