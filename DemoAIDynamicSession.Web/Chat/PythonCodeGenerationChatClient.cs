using System.Runtime.CompilerServices;
using System.Text;
using DemoAIDynamicSession.Web.CodeExecution;
using DemoAIDynamicSession.Web.CodeGeneration;
using Microsoft.Extensions.AI;

namespace DemoAIDynamicSession.Web.Chat;

/// <summary>
/// Adatta il flusso completo della demo all'interfaccia <see cref="IChatClient"/> richiesta dal
/// componente <c>AiChat</c>: eventuali file allegati al messaggio vengono caricati per primi nella
/// ACA Dynamic Session, poi il prompt viene inviato all'endpoint di generazione codice dell'ApiService
/// (Ollama, arricchito con un'anteprima dei file caricati), il codice Python restituito viene eseguito
/// nella stessa sessione, e la risposta finale mostra codice, output ed eventuali file prodotti come
/// link scaricabili.
/// </summary>
/// <remarks>
/// L'istanza è scoped (una per circuito Blazor): <see cref="_sessionId"/> viene generato alla prima
/// richiesta e riusato per tutta la conversazione, cosi' la sandbox ACA mantiene lo stato (variabili,
/// file caricati o generati) tra un messaggio e l'altro, esattamente come validato negli step precedenti.
/// </remarks>
public sealed class PythonCodeGenerationChatClient(
    PythonCodeGenerationApiClient codeGenerationApi,
    CodeExecutionApiClient codeExecutionApi) : IChatClient
{
    /// <summary>
    /// Estensioni di file testuali per cui vale la pena costruire un'anteprima da passare al modello.
    /// Per gli altri file (es. immagini, Excel) l'upload avviene comunque, ma senza anteprima.
    /// </summary>
    private static readonly HashSet<string> TextPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".txt", ".json", ".md", ".tsv", ".log",
    };

    private const int PreviewMaxLines = 20;
    private const int PreviewMaxChars = 2_000;

    private string? _sessionId;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var reply = await GenerateReplyAsync(messages, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var reply = await GenerateReplyAsync(messages, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private async Task<string> GenerateReplyAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var lastUserMessage = messages.LastOrDefault(message => message.Role == ChatRole.User);
        var prompt = lastUserMessage?.Text?.Trim();
        var attachments = lastUserMessage?.Contents.OfType<DataContent>().ToList() ?? [];

        if (string.IsNullOrWhiteSpace(prompt) && attachments.Count == 0)
        {
            return "Scrivi una richiesta (es. \"genera un CSV con vendite fittizie\") oppure allega un file " +
                "e chiedi di analizzarlo: LLM generera' il codice Python e verra' eseguito in una ACA Dynamic Session.";
        }

        var isFirstExecutionInSession = _sessionId is null;
        _sessionId ??= Guid.NewGuid().ToString("n");

        var (hints, uploadedFiles, uploadErrors) = await UploadAttachmentsAsync(attachments, _sessionId, cancellationToken);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            // Solo allegati, nessuna richiesta testuale: non ha senso generare codice, riportiamo solo l'upload.
            return FormatUploadOnlyReply(uploadedFiles, uploadErrors, _sessionId, isFirstExecutionInSession);
        }

        PythonCodeGenerationResult generation;
        try
        {
            generation = await codeGenerationApi.GenerateAsync(
                prompt,
                hints.Count > 0 ? hints : null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Non sono riuscito a generare il codice: {ex.Message}";
        }

        if (!generation.Success || string.IsNullOrWhiteSpace(generation.Code))
        {
            return FormatGenerationFailure(generation);
        }

        CodeExecutionResult? execution = null;
        string? executionError = null;
        try
        {
            execution = await codeExecutionApi.ExecuteAsync(
                new CodeExecutionInput(generation.Code, _sessionId),
                cancellationToken);
        }
        catch (Exception ex)
        {
            executionError = ex.Message;
        }

        IReadOnlyList<SessionFile> files = [];
        if (execution is { IsSuccess: true })
        {
            try
            {
                files = await codeExecutionApi.GetFilesAsync(_sessionId, cancellationToken);
            }
            catch (Exception)
            {
                // Non blocchiamo la risposta se l'elenco file fallisce: mostriamo comunque stdout/stderr.
            }
        }

        return FormatReply(
            generation,
            execution,
            executionError,
            files,
            _sessionId,
            isFirstExecutionInSession,
            uploadedFiles,
            uploadErrors);
    }

    /// <summary>
    /// Carica ogni file allegato al messaggio nella sessione ACA corrente (prima ancora di generare
    /// codice) e, per i tipi testuali riconosciuti, ne estrae un'anteprima da passare al modello
    /// insieme al prompt, cosi' il codice generato può leggere il file reale invece di inventare dati.
    /// </summary>
    private async Task<(List<AttachedFileHint> Hints, List<string> Uploaded, List<string> Errors)> UploadAttachmentsAsync(
        IReadOnlyList<DataContent> attachments,
        string sessionId,
        CancellationToken cancellationToken)
    {
        List<AttachedFileHint> hints = [];
        List<string> uploaded = [];
        List<string> errors = [];

        foreach (var attachment in attachments)
        {
            var fileName = string.IsNullOrWhiteSpace(attachment.Name) ? "file-senza-nome" : attachment.Name;
            var bytes = attachment.Data.ToArray();

            try
            {
                using var stream = new MemoryStream(bytes);
                await codeExecutionApi.UploadFileAsync(sessionId, fileName, stream, cancellationToken);
                uploaded.Add(fileName);
                hints.Add(new AttachedFileHint(fileName, BuildPreview(fileName, bytes)));
            }
            catch (Exception ex)
            {
                errors.Add($"{fileName}: {ex.Message}");
            }
        }

        return (hints, uploaded, errors);
    }

    private static string? BuildPreview(string fileName, byte[] data)
    {
        var extension = Path.GetExtension(fileName);
        if (!TextPreviewExtensions.Contains(extension))
        {
            return null;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            return null;
        }

        var preview = string.Join('\n', text.Split('\n').Take(PreviewMaxLines));
        return preview.Length > PreviewMaxChars ? preview[..PreviewMaxChars] : preview;
    }

    private static string FormatUploadOnlyReply(
        IReadOnlyList<string> uploadedFiles,
        IReadOnlyList<string> uploadErrors,
        string sessionId,
        bool isFirstExecutionInSession)
    {
        var builder = new StringBuilder();
        AppendUploadSection(builder, uploadedFiles, uploadErrors);
        builder.AppendLine();
        builder.AppendLine("Aggiungi una richiesta (es. \"analizza questo file e crea un grafico\") per generare ed eseguire il codice Python.");

        if (isFirstExecutionInSession && uploadedFiles.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"_Sessione ACA: `{sessionId}` — i file caricati restano disponibili nei prossimi messaggi di questa chat._");
        }

        return builder.ToString();
    }

    private static void AppendUploadSection(
        StringBuilder builder,
        IReadOnlyList<string> uploadedFiles,
        IReadOnlyList<string> uploadErrors)
    {
        if (uploadedFiles.Count == 0 && uploadErrors.Count == 0)
        {
            return;
        }

        if (uploadedFiles.Count > 0)
        {
            builder.AppendLine("📎 File caricati nella sandbox ACA:");
            foreach (var file in uploadedFiles)
            {
                builder.AppendLine($"- {file}");
            }
        }

        if (uploadErrors.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("⚠️ Alcuni file non sono stati caricati:");
            foreach (var error in uploadErrors)
            {
                builder.AppendLine($"- {error}");
            }
        }
    }

    private static string FormatGenerationFailure(PythonCodeGenerationResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Il modello non ha restituito codice Python valido, quindi non ho eseguito nulla.");
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            builder.AppendLine();
            builder.AppendLine($"Dettaglio: {result.Error}");
        }

        builder.AppendLine();
        builder.AppendLine("Risposta grezza del modello:");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(result.RawResponse);
        builder.AppendLine("```");
        return builder.ToString();
    }

    private static string FormatReply(
        PythonCodeGenerationResult generation,
        CodeExecutionResult? execution,
        string? executionError,
        IReadOnlyList<SessionFile> files,
        string sessionId,
        bool isFirstExecutionInSession,
        IReadOnlyList<string> uploadedFiles,
        IReadOnlyList<string> uploadErrors)
    {
        var builder = new StringBuilder();

        if (uploadedFiles.Count > 0 || uploadErrors.Count > 0)
        {
            AppendUploadSection(builder, uploadedFiles, uploadErrors);
            builder.AppendLine();
        }

        builder.AppendLine("Ho generato ed eseguito questo codice Python in una ACA Dynamic Session:");
        builder.AppendLine();
        builder.AppendLine("```python");
        builder.AppendLine(generation.Code);
        builder.AppendLine("```");

        builder.AppendLine();
        if (executionError is not null)
        {
            builder.AppendLine($"⚠️ Esecuzione fallita: {executionError}");
        }
        else if (execution is not null)
        {
            builder.AppendLine(execution.IsSuccess
                ? $"✅ Esecuzione completata (`{execution.ExecutorMode}`, {execution.DurationMs:0} ms)."
                : $"⚠️ Esecuzione terminata con stato `{execution.Status}`.");

            if (!string.IsNullOrWhiteSpace(execution.Stdout))
            {
                builder.AppendLine();
                builder.AppendLine("Output:");
                builder.AppendLine("```");
                builder.AppendLine(execution.Stdout.TrimEnd());
                builder.AppendLine("```");
            }

            if (!string.IsNullOrWhiteSpace(execution.Stderr))
            {
                builder.AppendLine();
                builder.AppendLine("Errori:");
                builder.AppendLine("```");
                builder.AppendLine(execution.Stderr.TrimEnd());
                builder.AppendLine("```");
            }
        }

        if (files.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("File generati (scaricabili):");
            foreach (var file in files)
            {
                var downloadUrl = $"/files/{Uri.EscapeDataString(sessionId)}/{Uri.EscapeDataString(file.FileName)}";
                builder.AppendLine($"- [{file.FileName}]({downloadUrl}) ({file.Size} byte)");
            }
        }
        else if (generation.ExpectedFiles.Count > 0 && execution is { IsSuccess: true })
        {
            builder.AppendLine();
            builder.AppendLine("Nessun file trovato nella sessione, anche se erano attesi: " +
                string.Join(", ", generation.ExpectedFiles));
        }

        if (!string.IsNullOrWhiteSpace(generation.Notes))
        {
            builder.AppendLine();
            builder.AppendLine($"Nota: {generation.Notes}");
        }

        if (isFirstExecutionInSession)
        {
            builder.AppendLine();
            builder.AppendLine($"_Sessione ACA: `{sessionId}` — i prossimi messaggi di questa chat riuseranno la stessa sandbox._");
        }

        return builder.ToString();
    }
}

