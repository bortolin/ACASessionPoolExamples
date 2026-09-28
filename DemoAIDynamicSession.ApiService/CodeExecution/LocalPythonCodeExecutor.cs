using System.Collections.Concurrent;
using System.Diagnostics;

namespace DemoAIDynamicSession.ApiService.CodeExecution;

/// <summary>
/// Fallback per la demo offline: esegue il codice con l'interprete Python locale.
/// Non è una sandbox isolata, viene abilitato solo in sviluppo quando il session pool
/// Azure non è configurato e serve unicamente a mostrare il flusso applicativo.
/// </summary>
public sealed class LocalPythonCodeExecutor(ILogger<LocalPythonCodeExecutor> logger) : ICodeExecutor
{
    private static readonly string[] CandidateExecutables = ["python3", "python"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, SessionHistory> _sessions = new();

    public string Mode => CodeExecutorModes.LocalFallback;

    public async Task<CodeExecutionResponse> ExecuteAsync(
        string code,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var history = _sessions.GetOrAdd(sessionId, _ => new SessionHistory());

        await history.Gate.WaitAsync(cancellationToken);
        try
        {
            // Le dynamic sessions mantengono vivo l'interprete tra le chiamate: localmente
            // lo emuliamo rieseguendo gli snippet precedenti e mostrando solo l'output nuovo.
            var script = string.Join("\n\n", history.Snippets.Append(code));

            var stopwatch = Stopwatch.StartNew();
            var (stdout, stderr, exitCode) = await RunPythonAsync(script, history.WorkingDirectory, cancellationToken);
            stopwatch.Stop();

            var newStdout = stdout.StartsWith(history.LastStdout, StringComparison.Ordinal)
                ? stdout[history.LastStdout.Length..]
                : stdout;

            if (exitCode == 0)
            {
                history.Snippets.Add(code);
                history.LastStdout = stdout;
            }

            return new CodeExecutionResponse(
                sessionId,
                exitCode == 0 ? "Succeeded" : "Failed",
                newStdout,
                stderr,
                null,
                stopwatch.Elapsed.TotalMilliseconds,
                Mode,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            history.Gate.Release();
        }
    }

    public Task<string> UploadFileAsync(
        string sessionId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        var history = _sessions.GetOrAdd(sessionId, _ => new SessionHistory());
        Directory.CreateDirectory(history.WorkingDirectory);

        var destinationPath = Path.Combine(history.WorkingDirectory, fileName);
        return WriteFileAsync(content, destinationPath, cancellationToken);
    }

    public Task<IReadOnlyList<SessionFile>> ListFilesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(sessionId, out var history) ||
            !Directory.Exists(history.WorkingDirectory))
        {
            return Task.FromResult<IReadOnlyList<SessionFile>>([]);
        }

        IReadOnlyList<SessionFile> files = Directory
            .EnumerateFiles(history.WorkingDirectory)
            .Where(path => !string.Equals(Path.GetFileName(path), "__snippet__.py", StringComparison.Ordinal))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new SessionFile(
                    info.Name,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(files);
    }

    public async Task<SessionFileContent> DownloadFileAsync(
        string sessionId,
        string fileName,
        CancellationToken cancellationToken)
    {
        if (fileName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new CodeExecutionException("Il nome del file non è valido.");
        }

        if (!_sessions.TryGetValue(sessionId, out var history))
        {
            throw new CodeExecutionException($"Sessione '{sessionId}' non trovata.");
        }

        var filePath = Path.Combine(history.WorkingDirectory, fileName);
        if (!File.Exists(filePath))
        {
            throw new CodeExecutionException($"File '{fileName}' non trovato nella sessione.");
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        return new SessionFileContent(bytes, ContentTypeResolver.FromFileName(fileName));
    }

    private static async Task<string> WriteFileAsync(Stream content, string destinationPath, CancellationToken cancellationToken)
    {
        await using var fileStream = File.Create(destinationPath);
        await content.CopyToAsync(fileStream, cancellationToken);
        return destinationPath;
    }

    private async Task<(string Stdout, string Stderr, int ExitCode)> RunPythonAsync(
        string script,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workingDirectory);
        var scriptPath = Path.Combine(workingDirectory, "__snippet__.py");
        await File.WriteAllTextAsync(scriptPath, script, cancellationToken);

        Exception? lastFailure = null;

        foreach (var executable in CandidateExecutables)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-I");
            startInfo.ArgumentList.Add(scriptPath);

            using var process = new Process { StartInfo = startInfo };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                logger.LogDebug(ex, "Interprete '{Executable}' non disponibile.", executable);
                continue;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(Timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new CodeExecutionException(
                    $"Esecuzione interrotta: superato il timeout di {Timeout.TotalSeconds:0} secondi.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            return (await stdoutTask, await stderrTask, process.ExitCode);
        }

        throw new CodeExecutionException(
            "Nessun interprete Python disponibile in locale. Configura 'DynamicSessions:PoolManagementEndpoint' " +
            "per eseguire il codice su Azure Container Apps dynamic sessions, oppure installa Python.",
            lastFailure);
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impossibile terminare il processo Python locale.");
        }
    }

    private sealed class SessionHistory
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public List<string> Snippets { get; } = [];

        public string LastStdout { get; set; } = string.Empty;

        public string WorkingDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "demo-dynamic-sessions", Guid.NewGuid().ToString("n"));
    }
}
