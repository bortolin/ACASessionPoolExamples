using System.ComponentModel;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Core.CodeInterpreter;

namespace DemoAIDynamicSession.ApiService.SemanticKernelDemo;

internal sealed class ObservedSessionsPythonPlugin(SessionsPythonPlugin innerPlugin)
{
    // Tetto di sicurezza per la demo: il modello può ispezionare il PDF e correggersi da solo,
    // ma non deve poter rieseguire codice all'infinito nella ACA Dynamic Session.
    private const int MaxExecutions = 4;

    // Il modello può richiedere più tool-call in parallelo nello stesso turno (parallel function
    // calling): contatore e lista vanno protetti, altrimenti due esecuzioni concorrenti possono
    // corrompere/perdere una voce e la timeline in UI risulta incompleta.
    private readonly Lock _lock = new();
    private readonly List<PythonExecutionTrace> _executions = [];
    private int _executionCount;

    public IReadOnlyList<PythonExecutionTrace> Executions
    {
        get
        {
            lock (_lock)
            {
                return _executions.OrderBy(execution => execution.Attempt).ToArray();
            }
        }
    }

    [KernelFunction("execute_python")]
    [Description("Esegue codice Python nella ACA Dynamic Session associata al documento caricato.")]
    public async Task<SessionsPythonCodeExecutionResult> ExecuteCodeAsync(
        [Description("Codice Python completo da eseguire.")] string code,
        CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref _executionCount);
        if (attempt > MaxExecutions)
        {
            throw new InvalidOperationException(
                $"Per restare una demo semplice, execute_python può essere chiamata al massimo {MaxExecutions} volte per richiesta.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var result = await innerPlugin.ExecuteCodeAsync(code, cancellationToken);

        var trace = new PythonExecutionTrace(
            attempt,
            code,
            result.Status ?? "Unknown",
            result.Result?.StdOut ?? string.Empty,
            result.Result?.StdErr ?? string.Empty,
            result.Result?.ExecutionResult?.ToString(),
            startedAt,
            DateTimeOffset.UtcNow);

        lock (_lock)
        {
            _executions.Add(trace);
        }

        return result;
    }
}

internal sealed record PythonExecutionTrace(
    int Attempt,
    string Code,
    string Status,
    string Stdout,
    string Stderr,
    string? Result,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
