using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace DemoAIDynamicSession.ApiService.CodeExecution;

public static class CodeExecutionEndpoints
{
    private const long MaxUploadBytes = 20 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCodeExecutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/code")
            .WithTags("CodeExecution");

        group.MapGet("/status", (
            IOptions<DynamicSessionsOptions> options,
            ICodeExecutor executor) => Results.Ok(BuildStatus(options.Value, executor)))
            .WithName("GetCodeExecutionStatus");

        group.MapGet("/samples", () => Results.Ok(CodeSamples.All))
            .WithName("GetCodeSamples");

        group.MapPost("/execute", async (
            CodeExecutionRequest request,
            ICodeExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var validationResult = Validate(request);
            if (validationResult is not null)
            {
                return validationResult;
            }

            if (executor.Mode == CodeExecutorModes.Disabled)
            {
                return Results.Problem(
                    detail: "Nessun motore di esecuzione disponibile. Configura 'DynamicSessions:PoolManagementEndpoint' " +
                            "per usare un session pool di Azure Container Apps.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Esecuzione codice non disponibile");
            }

            var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
                ? Guid.NewGuid().ToString("n")
                : request.SessionId.Trim();

            try
            {
                var result = await executor.ExecuteAsync(request.Code, sessionId, cancellationToken);
                return Results.Ok(result);
            }
            catch (CodeExecutionException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Esecuzione fallita");
            }
        })
        .WithName("ExecuteCode");

        group.MapGet("/files", async (
            string sessionId,
            ICodeExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var sessionValidation = ValidateSessionId(sessionId);
            if (sessionValidation is not null)
            {
                return sessionValidation;
            }

            if (executor.Mode == CodeExecutorModes.Disabled)
            {
                return ExecutorUnavailable("Lettura file non disponibile");
            }

            try
            {
                var files = await executor.ListFilesAsync(sessionId.Trim(), cancellationToken);
                return Results.Ok(files);
            }
            catch (CodeExecutionException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Lettura file fallita");
            }
        })
        .WithName("ListSessionFiles");

        group.MapGet("/files/{fileName}/content", async (
            string sessionId,
            string fileName,
            ICodeExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var sessionValidation = ValidateSessionId(sessionId);
            if (sessionValidation is not null)
            {
                return sessionValidation;
            }

            if (!IsValidFileName(fileName))
            {
                return Results.BadRequest("Il nome del file non è valido.");
            }

            if (executor.Mode == CodeExecutorModes.Disabled)
            {
                return ExecutorUnavailable("Download file non disponibile");
            }

            try
            {
                var file = await executor.DownloadFileAsync(sessionId.Trim(), fileName.Trim(), cancellationToken);
                return Results.File(file.Content, file.ContentType, fileName);
            }
            catch (CodeExecutionException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Download file fallito");
            }
        })
        .WithName("DownloadSessionFile");

        group.MapPost("/files", async (
            HttpRequest request,
            ICodeExecutor executor,
            CancellationToken cancellationToken) =>
        {
            var sessionId = request.Query["sessionId"].ToString();
            var sessionValidation = ValidateSessionId(sessionId);
            if (sessionValidation is not null)
            {
                return sessionValidation;
            }

            if (executor.Mode == CodeExecutorModes.Disabled)
            {
                return ExecutorUnavailable("Caricamento file non disponibile");
            }

            if (!request.HasFormContentType)
            {
                return Results.BadRequest("La richiesta deve essere multipart/form-data.");
            }

            var form = await request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest("Seleziona un file non vuoto da caricare.");
            }

            if (file.Length > MaxUploadBytes)
            {
                return Results.BadRequest($"Il file non può superare {MaxUploadBytes / 1024 / 1024} MB.");
            }

            var fileName = file.FileName.Trim();
            if (!IsValidFileName(fileName))
            {
                return Results.BadRequest("Il nome del file non è valido.");
            }

            try
            {
                await using var content = file.OpenReadStream();
                var path = await executor.UploadFileAsync(
                    sessionId.Trim(),
                    fileName,
                    content,
                    cancellationToken);

                return Results.Ok(new FileUploadResponse(fileName, path));
            }
            catch (CodeExecutionException ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Caricamento file fallito");
            }
        })
        .WithName("UploadSessionFile");

        return endpoints;
    }

    private static IResult ExecutorUnavailable(string title) =>
        Results.Problem(
            detail: "Nessun motore di esecuzione disponibile. Configura 'DynamicSessions:PoolManagementEndpoint' " +
                    "per usare un session pool di Azure Container Apps.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: title);

    private static IResult? ValidateSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.BadRequest("L'identificatore di sessione è obbligatorio.");
        }

        return sessionId.Trim().Length <= 128
            ? null
            : Results.BadRequest("L'identificatore di sessione non può superare 128 caratteri.");
    }

    private static bool IsValidFileName(string fileName) =>
        fileName.Length is > 0 and <= 255 &&
        fileName.IndexOfAny(['/', '\\']) < 0 &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static CodeExecutionStatus BuildStatus(DynamicSessionsOptions options, ICodeExecutor executor)
    {
        var mode = executor.Mode;

        var description = mode switch
        {
            CodeExecutorModes.AzureDynamicSessions =>
                "Il codice viene eseguito in una sandbox Hyper-V isolata di Azure Container Apps dynamic sessions.",
            CodeExecutorModes.LocalFallback =>
                "Session pool non configurato: il codice viene eseguito dall'interprete Python locale, senza isolamento.",
            _ =>
                "Esecuzione disabilitata: configura 'DynamicSessions:PoolManagementEndpoint' per usare un session pool."
        };

        return new CodeExecutionStatus(
            mode,
            options.IsConfigured,
            options.PoolManagementEndpoint,
            options.ApiVersion,
            mode == CodeExecutorModes.AzureDynamicSessions,
            description);
    }

    private static IResult? Validate(CodeExecutionRequest request)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, true))
        {
            return null;
        }

        var errors = results
            .SelectMany(
                result => result.MemberNames.DefaultIfEmpty(string.Empty),
                (result, memberName) => new { memberName, result.ErrorMessage })
            .GroupBy(item => item.memberName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.ErrorMessage ?? "Valore non valido.").ToArray());

        return Results.ValidationProblem(errors);
    }
}
