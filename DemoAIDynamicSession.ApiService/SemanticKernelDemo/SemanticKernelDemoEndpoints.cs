using Microsoft.Extensions.Caching.Memory;

namespace DemoAIDynamicSession.ApiService.SemanticKernelDemo;

public static class SemanticKernelDemoEndpoints
{
    private const long MaxPdfBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan WorkbookLifetime = TimeSpan.FromMinutes(30);

    public static IEndpointRouteBuilder MapSemanticKernelDemoEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/semantic-kernel-demo")
            .WithTags("SemanticKernelDemo");

        // Endpoint per l'elaborazione dei PDF e la generazione di file Excel utilizzando Semantic Kernel e ACA Dynamic Sessions.
        group.MapPost("/pdf-to-excel", ProcessPdfAsync)
            .DisableAntiforgery()
            .WithName("RunSemanticKernelPdfToExcel");

        group.MapGet("/files/{operationId}", (
            string operationId,
            IMemoryCache cache) =>
        {
            if (!Guid.TryParseExact(operationId, "N", out _))
            {
                return Results.BadRequest("L'identificatore dell'operazione non è valido.");
            }

            if (!cache.TryGetValue<GeneratedWorkbook>(CacheKey(operationId), out var workbook) ||
                workbook is null)
            {
                return Results.NotFound("Il file non è disponibile o è scaduto.");
            }

            return Results.File(
                workbook.Content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                workbook.FileName);
        })
        .WithName("DownloadSimulatedContactsWorkbook");

        return endpoints;
    }

    // Gestisce la richiesta di elaborazione di un PDF e la generazione di un file Excel.
    // Restituisce un risultato contenente l'identificatore dell'operazione, l'ID della sessione, il nome del file generato e lo stato dell'elaborazione.
    private static async Task<IResult> ProcessPdfAsync(
        HttpRequest request,
        IMemoryCache cache,
        SemanticKernelPdfToExcelService processor,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest("La richiesta deve essere multipart/form-data.");
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest("Seleziona un file PDF non vuoto.");
        }

        if (file.Length > MaxPdfBytes)
        {
            return Results.BadRequest($"Il PDF non può superare {MaxPdfBytes / 1024 / 1024} MB.");
        }

        var fileName = Path.GetFileName(file.FileName);
        if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("La demo accetta esclusivamente file con estensione .pdf.");
        }

        await using (var source = file.OpenReadStream())
        {
            var signature = new byte[5];
            var bytesRead = await source.ReadAsync(signature, cancellationToken);
            if (bytesRead != signature.Length || !signature.AsSpan().SequenceEqual("%PDF-"u8))
            {
                return Results.BadRequest("Il file caricato non contiene una firma PDF valida.");
            }
        }

        // Genera un identificatore univoco per l'operazione di elaborazione del PDF.
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            await using var source = file.OpenReadStream();

            // Avvia l'elaborazione del PDF utilizzando il servizio SemanticKernelPdfToExcelService.
            var processing = await processor.ProcessAsync(
                operationId,
                fileName,
                source,
                cancellationToken);

            // Memorizza il risultato dell'elaborazione nella cache per un rapido accesso successivo.
            var workbook = new GeneratedWorkbook(
                processing.Workbook,
                processing.GeneratedFileName,
                DateTimeOffset.UtcNow.Add(WorkbookLifetime));

            // Aggiunge il risultato dell'elaborazione alla cache.    
            cache.Set(
                CacheKey(operationId),
                workbook,
                new MemoryCacheEntryOptions { AbsoluteExpiration = workbook.ExpiresAt });

            // Restituisce la risposta contenente i dettagli dell'elaborazione del PDF e del file Excel generato.
            return Results.Ok(new PdfToExcelResponse(
                operationId,
                processing.SessionId,
                IsSimulated: false,
                fileName,
                processing.GeneratedFileName,
                processing.Contacts.Count,
                DateTimeOffset.UtcNow,
                processing.KernelResponse,
                processing.Contacts,
                processing.Steps));
        }
        catch (OperationCanceledException opc)
        {  
            return Results.Problem(
                detail: "L'elaborazione è stata annullata. " + opc.Message,
                statusCode: StatusCodes.Status499ClientClosedRequest,
                title: "Richiesta annullata");
        }
        catch (SemanticKernelDemoProcessingException ex)
        {
            loggerFactory
                .CreateLogger(typeof(SemanticKernelDemoEndpoints))
                .LogError(ex, "La demo Semantic Kernel non è stata completata");

            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway,
                title: "Elaborazione Semantic Kernel fallita");
        }
    }

    private static string CacheKey(string operationId) => $"semantic-kernel-demo:{operationId}";
}
