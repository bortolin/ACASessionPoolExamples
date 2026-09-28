using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace DemoAIDynamicSession.ApiService.CodeGeneration;

/// <summary>
/// Endpoint didattico: fa generare al modello uno script Python in risposta a una richiesta in
/// linguaggio naturale, ma non lo esegue. Serve a validare la generazione di codice strutturato
/// prima di collegarla, in uno step successivo, a una ACA Dynamic Session.
/// </summary>
public static class PythonCodeGenerationEndpoints
{
    private const string SystemPrompt = """
        Sei un generatore di script Python per una demo didattica.
        Rispondi SOLO in questo formato a blocchi delimitati, senza altro testo prima o dopo:

        ---CODE---
        <qui il codice Python completo, scritto normalmente su più righe, SENZA fence markdown>
        ---FILES---
        <elenco dei file attesi separati da virgola, es: vendite.csv, summary.txt>
        ---NOTES---
        <breve nota in italiano su cosa fa lo script>
        ---END---

        Regole importanti:
        - nel blocco ---CODE--- scrivi il codice Python così com'è, con newline reali tra le righe:
          NON incapsularlo in una stringa JSON e NON raddoppiare i backslash;
        - il codice deve essere autosufficiente ed eseguibile con "python script.py";
        - usa librerie della standard library (es. csv, json, pathlib, random, datetime, statistics)
          oppure, se serve un grafico o un'analisi dati più avanzata, le librerie già installate
          nella sandbox: pandas, numpy, matplotlib (usa il backend "Agg" e salva il grafico con
          plt.savefig('nome.png'), non usare plt.show());
        - PRIMA di rispondere, ricontrolla riga per riga che ogni modulo usato nel codice
          (es. random, csv, json, math, datetime, statistics, os, re, pandas, numpy, matplotlib)
          sia stato importato con un "import <modulo>" all'inizio dello script: dimenticare un
          import è l'errore più comune, non farlo;
        - crea i file solo nella directory corrente, con path relativi;
        - non deve accedere alla rete;
        - non deve usare subprocess, os.system, eval, exec;
        - non deve leggere file esterni preesistenti;
        - se non ci sono file attesi, lascia il blocco ---FILES--- vuoto.
        """;

    /// <summary>
    /// Moduli della standard library usati più di frequente nel codice generato ma talvolta
    /// dimenticati dal modello (in particolare "random"). Se il codice usa "modulo." ma non lo
    /// importa esplicitamente, l'import viene aggiunto automaticamente come rete di sicurezza,
    /// per non far fallire l'esecuzione per una semplice dimenticanza del modello.
    /// </summary>
    private static readonly string[] CommonStdlibModules =
    [
        "random", "csv", "json", "math", "datetime", "statistics",
        "os", "re", "collections", "itertools", "string", "time", "pathlib",
    ];

    public static IEndpointRouteBuilder MapPythonCodeGenerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/codegen")
            .WithTags("CodeGeneration");

        group.MapPost("/python", async (
            PythonCodeGenerationRequest request,
            IChatClient chatClient,
            CancellationToken cancellationToken) =>
        {
            var validationResult = Validate(request);
            if (validationResult is not null)
            {
                return validationResult;
            }

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, SystemPrompt),
            };

            if (request.AvailableFiles is { Count: > 0 })
            {
                messages.Add(new ChatMessage(ChatRole.User, BuildAvailableFilesContext(request.AvailableFiles)));
            }

            messages.Add(new ChatMessage(ChatRole.User, request.Prompt.Trim()));

            try
            {
                var response = await chatClient.GetResponseAsync(messages, options: null, cancellationToken);
                return Results.Ok(ParseResponse(response.Text));
            }
            catch (OperationCanceledException)
            {
                return Results.Problem(
                    detail: "La richiesta al modello è stata annullata.",
                    statusCode: StatusCodes.Status499ClientClosedRequest,
                    title: "Richiesta annullata");
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Errore durante la generazione del codice");
            }
        })
        .WithName("GeneratePythonCode");

        return endpoints;
    }

    /// <summary>
    /// Costruisce un messaggio di contesto che elenca i file già presenti nella sandbox (caricati
    /// dall'utente prima del prompt), con un'anteprima quando disponibile, così il modello preferisce
    /// leggerli con open()/pandas invece di inventare dati fittizi.
    /// </summary>
    private static string BuildAvailableFilesContext(IReadOnlyList<AttachedFileHint> files)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine(
            "Nella working directory della sandbox sono già presenti questi file, caricati dall'utente " +
            "prima di questo messaggio. Se la richiesta si riferisce a uno di essi, leggilo con " +
            "open()/pandas invece di generare dati fittizi:");

        foreach (var file in files)
        {
            builder.AppendLine();
            builder.AppendLine($"- {file.FileName}");
            if (!string.IsNullOrWhiteSpace(file.Preview))
            {
                builder.AppendLine("  Anteprima del contenuto:");
                builder.AppendLine("  ```");
                foreach (var line in file.Preview.Split('\n'))
                {
                    builder.AppendLine($"  {line}");
                }
                builder.AppendLine("  ```");
            }
            else
            {
                builder.AppendLine("  (file binario o anteprima non disponibile: apri il file per ispezionarne il contenuto)");
            }
        }

        return builder.ToString();
    }

    private static IResult? Validate(PythonCodeGenerationRequest request)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, true))
        {
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

        return null;
    }

    /// <summary>
    /// Estrae codice/file attesi/note dal formato a blocchi delimitati (---CODE---, ---FILES---,
    /// ---NOTES---, ---END---). Rispetto a un formato JSON con il codice come stringa, questo
    /// approccio evita che il modello debba fare un doppio escaping dei newline/backslash nel
    /// codice Python: i modelli locali più piccoli sbagliano spesso questo escaping, producendo
    /// codice con newline letterali dentro le stringhe (causa comune di "unterminated string
    /// literal" / "unterminated f-string literal" all'esecuzione).
    /// </summary>
    private static PythonCodeGenerationResponse ParseResponse(string rawText)
    {
        var code = ExtractBlock(rawText, "---CODE---", ["---FILES---", "---NOTES---", "---END---"]);
        if (string.IsNullOrWhiteSpace(code))
        {
            return new PythonCodeGenerationResponse(
                Success: false,
                Code: null,
                ExpectedFiles: [],
                Notes: null,
                Error: "Il modello non ha restituito un blocco ---CODE--- riconoscibile.",
                RawResponse: rawText);
        }

        code = StripMarkdownFence(code);
        code = EnsureCommonImports(code);

        var filesBlock = ExtractBlock(rawText, "---FILES---", ["---NOTES---", "---END---"]);
        var expectedFiles = (filesBlock ?? string.Empty)
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(file => file.TrimStart('-', '*', ' ').Trim())
            .Where(file => file.Length > 0)
            .ToList();

        var notes = ExtractBlock(rawText, "---NOTES---", ["---END---"])?.Trim();

        return new PythonCodeGenerationResponse(
            Success: true,
            Code: code,
            ExpectedFiles: expectedFiles,
            Notes: string.IsNullOrWhiteSpace(notes) ? null : notes,
            Error: null,
            RawResponse: rawText);
    }

    /// <summary>
    /// Estrae il testo tra <paramref name="startMarker"/> e il primo tra i <paramref name="endMarkers"/>
    /// che compare dopo di esso (o fino a fine testo se nessuno di essi è presente).
    /// </summary>
    private static string? ExtractBlock(string text, string startMarker, IReadOnlyList<string> endMarkers)
    {
        var startIndex = text.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        var contentStart = startIndex + startMarker.Length;

        var endIndex = -1;
        foreach (var endMarker in endMarkers)
        {
            var candidate = text.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
            if (candidate >= 0 && (endIndex < 0 || candidate < endIndex))
            {
                endIndex = candidate;
            }
        }

        var content = endIndex >= 0
            ? text[contentStart..endIndex]
            : text[contentStart..];

        return content.Trim('\r', '\n');
    }

    private static string StripMarkdownFence(string code)
    {
        var trimmed = code.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        var withoutOpeningFence = trimmed[(firstNewline + 1)..];
        var closingFenceIndex = withoutOpeningFence.LastIndexOf("```", StringComparison.Ordinal);
        return (closingFenceIndex >= 0 ? withoutOpeningFence[..closingFenceIndex] : withoutOpeningFence).Trim();
    }

    /// <summary>
    /// Rete di sicurezza contro l'errore più comune del modello: usare un modulo stdlib
    /// (tipicamente "random") senza importarlo. Se il codice contiene "modulo." per uno dei
    /// <see cref="CommonStdlibModules"/> ma non ha già un "import modulo" (o "from modulo import"),
    /// l'import viene aggiunto in testa allo script.
    /// </summary>
    private static string EnsureCommonImports(string code)
    {
        var missingImports = new List<string>();

        foreach (var module in CommonStdlibModules)
        {
            var isUsed = Regex.IsMatch(code, $@"\b{module}\.\w", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!isUsed)
            {
                continue;
            }

            var alreadyImported = Regex.IsMatch(
                code,
                $@"^\s*(import\s+{module}\b|from\s+{module}\b|import\s+\w[\w.]*\s+as\s+{module}\b)",
                RegexOptions.Multiline,
                TimeSpan.FromSeconds(1));

            if (!alreadyImported)
            {
                missingImports.Add(module);
            }
        }

        return missingImports.Count == 0
            ? code
            : string.Join('\n', missingImports.Select(module => $"import {module}")) + "\n" + code;
    }
}

public sealed class PythonCodeGenerationRequest
{
    [Required(ErrorMessage = "La richiesta in linguaggio naturale è obbligatoria.")]
    [StringLength(2_000, ErrorMessage = "La richiesta non può superare 2000 caratteri.")]
    public string Prompt { get; set; } = string.Empty;

    /// <summary>File già caricati nella sandbox prima di questo prompt (upload esplicito dell'utente).</summary>
    public IReadOnlyList<AttachedFileHint>? AvailableFiles { get; set; }
}

/// <summary>Nome e anteprima testuale (facoltativa) di un file già presente nella sessione ACA.</summary>
public sealed record AttachedFileHint(string FileName, string? Preview);

public sealed record PythonCodeGenerationResponse(
    bool Success,
    string? Code,
    IReadOnlyList<string> ExpectedFiles,
    string? Notes,
    string? Error,
    string RawResponse);
