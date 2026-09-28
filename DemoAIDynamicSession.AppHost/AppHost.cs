var builder = DistributedApplication.CreateBuilder(args);

var ollamaMode = builder.Configuration["Ollama:Mode"]?.Trim().ToLowerInvariant();
ollamaMode = string.IsNullOrWhiteSpace(ollamaMode) ? "aspire" : ollamaMode;

var apiService = builder.AddProject<Projects.DemoAIDynamicSession_ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

if (ollamaMode == "aspire")
{
    // Il container Ollama viene creato solo in modalità Aspire.
    var ollama = builder.AddOllama("ollama")
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent);

    var importModel = ollama.AddModel("import-agent", "qwen2.5-coder:7b");
    apiService = apiService
        .WithReference(importModel)
        .WaitFor(importModel);
}
else if (ollamaMode != "local")
{
    throw new InvalidOperationException(
        $"Valore non valido per Ollama:Mode: '{ollamaMode}'. Usa 'local' oppure 'aspire'.");
}

// Endpoint di management del session pool di Azure Container Apps dynamic sessions.
// Impostalo con: dotnet user-secrets set "DynamicSessions:PoolManagementEndpoint" "https://<region>.dynamicsessions.io/subscriptions/<sub>/resourceGroups/<rg>/sessionPools/<pool>"
// Se assente, in sviluppo l'API usa il fallback di esecuzione locale.
var sessionPoolEndpoint = builder.Configuration["DynamicSessions:PoolManagementEndpoint"];

if (!string.IsNullOrWhiteSpace(sessionPoolEndpoint))
{
    apiService.WithEnvironment("DynamicSessions__PoolManagementEndpoint", sessionPoolEndpoint);
}

builder.AddProject<Projects.DemoAIDynamicSession_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(apiService)
    .WaitFor(apiService);

builder.Build().Run();
