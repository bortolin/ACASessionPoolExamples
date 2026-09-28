using Azure.Core;
using DemoAIDynamicSession.ApiService.CodeExecution;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Plugins.Core.CodeInterpreter;

namespace DemoAIDynamicSession.ApiService.SemanticKernelDemo;

public sealed class SemanticKernelPdfToExcelService(
    ICodeExecutor codeExecutor,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IServiceProvider services,
    IOptions<DynamicSessionsOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<SemanticKernelPdfToExcelService> logger)
{
    private const string InputFileName = "input.pdf";
    private const string InputFilePath = "/mnt/data/input.pdf";
    private const string OutputFileName = "contatti.xlsx";
    private const string OutputFilePath = "/mnt/data/contatti.xlsx";
    private static readonly string[] TokenScopes = ["https://dynamicsessions.io/.default"];

    private const string SystemPrompt = """
        Sei un agente che trasforma un PDF di contatti in un file Excel, ragionando passo dopo passo.
        Per leggere il PDF ed eseguire codice devi usare la funzione "execute_python": ogni volta che
        la chiami, il codice viene eseguito in una ACA Dynamic Session isolata, con accesso al
        filesystem della sessione.

        Contesto della sessione:
        - il PDF da elaborare si trova in /mnt/data/input.pdf;
        - devi creare il file /mnt/data/contatti.xlsx con un unico foglio di lavoro;
        - la prima riga del foglio deve contenere esattamente queste intestazioni, in questo ordine:
          Nome, Cognome, Azienda, Ruolo, Email, Telefono;
        - le righe successive devono contenere un contatto ciascuna, con i campi non trovati lasciati
          vuoti.

        Librerie Python già installate nella sandbox: fitz (PyMuPDF, per leggere il testo del PDF) e
        openpyxl (per creare il file .xlsx). Non hai accesso alla rete.

        Lavora per passi: se ti serve capire come è strutturato il testo del PDF prima di scrivere lo
        script definitivo, esegui prima un codice che stampa il testo estratto, poi scrivi lo script
        che genera il file Excel. Quando il file è stato creato con successo, rispondi con una frase
        breve in italiano che riassume quanti contatti hai estratto e come hai interpretato il formato
        del documento.
        """;

    private const string UserPromptTemplate =
        "Elabora il file \"{0}\", già caricato in /mnt/data/input.pdf, ed estrai i contatti.";

    private readonly DynamicSessionsOptions _options = options.Value;

    public async Task<PdfToExcelProcessingResult> ProcessAsync(
        string operationId,
        string sourceFileName,
        Stream source,
        CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || codeExecutor.Mode != CodeExecutorModes.AzureDynamicSessions)
        {
            throw new SemanticKernelDemoProcessingException(
                "Configura un pool ACA Dynamic Sessions per eseguire la demo.");
        }

        var credential = services.GetService<TokenCredential>()
            ?? throw new SemanticKernelDemoProcessingException(
                "Le credenziali Azure per ACA Dynamic Sessions non sono disponibili.");

        var sessionId = Guid.NewGuid().ToString("N");

        try
        {
            var uploadedPath = await codeExecutor.UploadFileAsync(
                sessionId,
                InputFileName,
                source,
                cancellationToken);

            if (!string.Equals(uploadedPath, InputFilePath, StringComparison.Ordinal))
            {
                throw new SemanticKernelDemoProcessingException(
                    $"Il PDF è stato caricato in un percorso inatteso: {uploadedPath}.");
            }

            // Crea il plugin delle sessioni ACA Dynamic Sessions
            // Aggiunge il plugin delle sessioni al kernel per permettere l'esecuzione di codice Python nelle sessioni ACA Dynamic Sessions.
            var sessionsPlugin = CreateSessionsPlugin(sessionId, credential);

            var kernelBuilder = Kernel.CreateBuilder();
            AddChatCompletion(kernelBuilder, credential);
            var kernel = kernelBuilder.Build();

            // Aggiunge il plugin delle sessioni al kernel
            // ObservedSessionsPythonPlugin permette di osservare le esecuzioni di codice Python nelle sessioni ACA Dynamic Sessions.
            var observedPlugin = new ObservedSessionsPythonPlugin(sessionsPlugin);
            kernel.Plugins.AddFromObject(observedPlugin, "SessionsPython");

            var chatCompletionService = kernel.GetRequiredService<IChatCompletionService>();
            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(SystemPrompt);
            chatHistory.AddUserMessage(string.Format(UserPromptTemplate, sourceFileName));

            var executionSettings = new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            };

            // Esegue la richiesta di completamento chat al modello, che può includere l'esecuzione di codice Python nelle sessioni ACA Dynamic Sessions.
            var response = await chatCompletionService.GetChatMessageContentAsync(
                chatHistory,
                executionSettings,
                kernel,
                cancellationToken);

            logger.LogInformation(
                "Risposta finale del modello: {Response}",
                Truncate(response.Content, 1_000));

            // Verifica se ci sono esecuzioni di codice Python osservate dal plugin nelle sessioni ACA Dynamic Sessions.
            if (observedPlugin.Executions.Count == 0)
            {
                throw new SemanticKernelDemoProcessingException(
                    "Il modello non ha invocato execute_python: nessun codice è stato eseguito nella ACA Dynamic Session.");
            }

            // Elenca i file presenti nella sessione ACA Dynamic Sessions per verificare se il file di output è stato creato.
            var files = await codeExecutor.ListFilesAsync(sessionId, cancellationToken);
            if (!files.Any(file =>
                    string.Equals(file.FileName, OutputFileName, StringComparison.OrdinalIgnoreCase)))
            {
                var lastExecution = observedPlugin.Executions[^1];
                throw new SemanticKernelDemoProcessingException(
                    $"Il modello ha eseguito {observedPlugin.Executions.Count} volte codice Python, ma " +
                    $"{OutputFilePath} non è stato creato. Ultimo stderr: {Truncate(lastExecution.Stderr, 2_000)}");
            }

            // Scarica il file di output dalla sessione ACA Dynamic Sessions e legge i contatti dal file Excel.
            var workbookFile = await codeExecutor.DownloadFileAsync(
                sessionId,
                OutputFileName,
                cancellationToken);
            var workbook = workbookFile.Content;
            var contacts = ExcelContactsReader.Read(workbook);

            if (contacts.Count == 0)
            {
                throw new SemanticKernelDemoProcessingException(
                    "Il file Excel è stato creato, ma non contiene contatti riconoscibili.");
            }

            // Restituisce il risultato dell'elaborazione, inclusi i contatti letti dal file Excel e i passaggi di esecuzione del codice Python.
            return new PdfToExcelProcessingResult(
                sessionId,
                OutputFileName,
                response.Content ?? "(il modello non ha fornito una risposta testuale)",
                workbook,
                contacts,
                BuildSteps(sourceFileName, sessionId, observedPlugin.Executions));
        }
        catch (KernelFunctionCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (KernelFunctionCanceledException ex)
        {
            logger.LogError(
                ex,
                "Esecuzione Python annullata nella ACA Dynamic Session {SessionId}",
                sessionId);
            throw new SemanticKernelDemoProcessingException(
                "L'esecuzione Python nella ACA Dynamic Session è stata interrotta prima del completamento. " +
                "Il pool potrebbe essere ancora in avvio oppure aver superato il timeout configurato.",
                ex);
        }
        catch (SemanticKernelDemoProcessingException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(
                ex,
                "Timeout durante il flusso Semantic Kernel per l'operazione {OperationId}",
                operationId);
            throw new SemanticKernelDemoProcessingException(
                "Una chiamata al modello o alla ACA Dynamic Session ha superato il timeout configurato. " +
                "Con un modello locale (Ollama) la generazione può richiedere più tempo del previsto: riprova, " +
                "oppure aumenta i timeout in Program.cs.",
                ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Elaborazione Semantic Kernel fallita per l'operazione {OperationId}", operationId);
            throw new SemanticKernelDemoProcessingException(
                $"Il flusso Semantic Kernel non è stato completato: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Registra un vero servizio di chat completion nel Kernel. Usa il connettore nativo di
    /// Semantic Kernel (Azure OpenAI, o OpenAI-compatibile per Ollama) invece del generico ponte
    /// IChatClient → IChatCompletionService: solo i connettori nativi eseguono realmente il loop di
    /// function-calling automatico richiesto da FunctionChoiceBehavior.Auto().
    /// </summary>
    private void AddChatCompletion(IKernelBuilder kernelBuilder, TokenCredential credential)
    {
        var azureEndpoint = configuration["AzureOpenAI:Endpoint"];
        var azureDeployment = configuration["AzureOpenAI:Deployment"];
        var azureApiKey = configuration["AzureOpenAI:ApiKey"];

        if (!string.IsNullOrWhiteSpace(azureEndpoint) && !string.IsNullOrWhiteSpace(azureDeployment))
        {
            if (string.IsNullOrWhiteSpace(azureApiKey))
            {
                kernelBuilder.AddAzureOpenAIChatCompletion(azureDeployment, azureEndpoint, credential);
            }
            else
            {
                kernelBuilder.AddAzureOpenAIChatCompletion(azureDeployment, azureEndpoint, azureApiKey);
            }

            return;
        }

        var ollamaEndpoint = configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
        var ollamaModel = configuration["Ollama:Model"] ?? "qwen2.5-coder:7b";

        kernelBuilder.AddOpenAIChatCompletion(
            modelId: ollamaModel,
            endpoint: new Uri($"{ollamaEndpoint.TrimEnd('/')}/v1"),
            apiKey: "ollama");
    }

    private SessionsPythonPlugin CreateSessionsPlugin(
        string sessionId,
        TokenCredential credential)
    {
        var endpoint = new Uri(_options.PoolManagementEndpoint!);
        var settings = new SessionsPythonSettings(sessionId, endpoint)
        {
            TimeoutInSeconds = _options.TimeoutSeconds,
            AllowedDomains = [endpoint.Host],
        };

        // Crea e restituisce un'istanza del plugin Python per le sessioni ACA Dynamic Sessions.
        return new SessionsPythonPlugin(
            settings,
            new NamedHttpClientFactory(httpClientFactory, "semantic-kernel-sessions"),
            token => GetAccessTokenAsync(credential, token),
            loggerFactory);
    }

    // Costruisce la lista dei passaggi di elaborazione, inclusi i passaggi di esecuzione del codice Python nelle sessioni ACA Dynamic Sessions.
    private static IReadOnlyList<ProcessingStep> BuildSteps(
        string sourceFileName,
        string sessionId,
        IReadOnlyList<PythonExecutionTrace> executions)
    {
        var steps = new List<ProcessingStep>
        {
            new(1, "PDF ricevuto", $"{sourceFileName} è stato validato dall'ApiService.", "completed", "ASP.NET Core"),
            new(2, "File caricato", $"L'API ACA ha caricato il PDF in {InputFilePath} nella sessione {sessionId[..8]}…", "completed", "ACA Dynamic Sessions"),
            new(3, "Plugin registrato", "SessionsPythonPlugin è stato registrato nel Kernel come funzione execute_python, a disposizione del modello.", "completed", "Semantic Kernel"),
        };

        var order = 4;
        foreach (var execution in executions)
        {
            var executionDetail = $"""
                Codice:
                {Truncate(execution.Code, 4_000)}

                stdout:
                {Truncate(execution.Stdout, 2_000)}

                stderr:
                {Truncate(execution.Stderr, 2_000)}
                """;

            var succeeded = string.Equals(execution.Status, "Succeeded", StringComparison.OrdinalIgnoreCase);
            steps.Add(new ProcessingStep(
                order++,
                $"Turno {execution.Attempt}: il modello esegue Python",
                $"Il modello ha deciso di chiamare execute_python (stato: {execution.Status}).",
                succeeded ? "completed" : "failed",
                "SessionsPythonPlugin",
                executionDetail));
        }

        steps.Add(new ProcessingStep(
            order,
            "Excel scaricato",
            $"{OutputFilePath} è stato verificato e scaricato dall'ApiService.",
            "completed",
            "ACA Dynamic Sessions"));

        return steps;
    }

    // Ottiene un token di accesso per autenticare le richieste alle sessioni ACA Dynamic Sessions.
    private static async Task<string> GetAccessTokenAsync(
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(TokenScopes),
            cancellationToken);
        return token.Token;
    }

    // Trunca una stringa a una lunghezza massima specificata, aggiungendo "…" se viene troncata.
    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(vuoto)";
        }

        return value.Length <= maxLength ? value : $"{value[..maxLength]}\n…";
    }
    private sealed class NamedHttpClientFactory(
        IHttpClientFactory innerFactory,
        string clientName) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => innerFactory.CreateClient(clientName);
    }
}

public sealed record PdfToExcelProcessingResult(
    string SessionId,
    string GeneratedFileName,
    string KernelResponse,
    byte[] Workbook,
    IReadOnlyList<ExtractedContact> Contacts,
    IReadOnlyList<ProcessingStep> Steps);

public sealed class SemanticKernelDemoProcessingException : Exception
{
    public SemanticKernelDemoProcessingException(string message) : base(message)
    {
    }

    public SemanticKernelDemoProcessingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
