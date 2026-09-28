namespace DemoAIDynamicSession.Web.SemanticKernelDemo;

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
    string? Detail);

public sealed record PdfToExcelResult(
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
