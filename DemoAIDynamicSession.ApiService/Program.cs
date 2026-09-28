using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using DemoAIDynamicSession.ApiService.Chat;
using DemoAIDynamicSession.ApiService.CodeExecution;
using DemoAIDynamicSession.ApiService.CodeGeneration;
using DemoAIDynamicSession.ApiService.SemanticKernelDemo;
using DemoAIDynamicSession.ApiService.SessionManagement;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddMemoryCache();
#pragma warning disable EXTEXP0001
builder.Services
    .AddHttpClient("semantic-kernel-sessions", (services, client) =>
    {
        var dynamicSessionsOptions = services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<DynamicSessionsOptions>>()
            .Value;
        client.Timeout = TimeSpan.FromSeconds(400);
    })
    .RemoveAllResilienceHandlers()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
#pragma warning restore EXTEXP0001
builder.Services.AddScoped<SemanticKernelPdfToExcelService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.AddCodeExecution();
builder.AddSessionManagement();

var azureOpenAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
var azureOpenAiDeployment = builder.Configuration["AzureOpenAI:Deployment"];
var azureOpenAiApiKey = builder.Configuration["AzureOpenAI:ApiKey"];

if (!string.IsNullOrWhiteSpace(azureOpenAiEndpoint) &&
    !string.IsNullOrWhiteSpace(azureOpenAiDeployment))
{
    var azureOpenAiClient = string.IsNullOrWhiteSpace(azureOpenAiApiKey)
        ? new AzureOpenAIClient(new Uri(azureOpenAiEndpoint), new DefaultAzureCredential())
        : new AzureOpenAIClient(new Uri(azureOpenAiEndpoint), new AzureKeyCredential(azureOpenAiApiKey));

    builder.Services.AddChatClient(
        azureOpenAiClient.GetChatClient(azureOpenAiDeployment).AsIChatClient());
}
else
{
    var ollamaMode = builder.Configuration["Ollama:Mode"]?.Trim().ToLowerInvariant();
    ollamaMode = string.IsNullOrWhiteSpace(ollamaMode) ? "aspire" : ollamaMode;

    if (ollamaMode == "local")
    {
        var ollamaEndpoint = builder.Configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
        var ollamaModel = builder.Configuration["Ollama:Model"] ?? "qwen2.5-coder:7b";

        builder.AddOllamaApiClient("import-agent", settings =>
            {
                settings.Endpoint = new Uri(ollamaEndpoint);
                settings.SelectedModel = ollamaModel;
            })
            .AddChatClient();
    }
    else if (ollamaMode == "aspire")
    {
        builder.AddOllamaApiClient("import-agent")
            .AddChatClient();
    }
    else
    {
        throw new InvalidOperationException(
            $"Valore non valido per Ollama:Mode: '{ollamaMode}'. Usa 'local' oppure 'aspire'.");
    }
}
#pragma warning disable EXTEXP0001
builder.Services
    .AddHttpClient("import-agent_httpClient", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .RemoveAllResilienceHandlers()
    .AddStandardResilienceHandler(options =>
    {
        // Con l'agente Semantic Kernel una singola richiesta può generare più chiamate al modello
        // (una per ogni turno di ragionamento/function-calling): con un modello locale (Ollama) su
        // CPU una sola di queste chiamate può richiedere diversi minuti, quindi i timeout vanno
        // tenuti larghi per evitare cancellazioni premature (TaskCanceledException).
        options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(15);
    });
#pragma warning restore EXTEXP0001

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "API service is running.");

app.MapDemoChatEndpoints();
app.MapPythonCodeGenerationEndpoints();
app.MapCodeExecutionEndpoints();
app.MapSemanticKernelDemoEndpoints();
app.MapSessionManagementEndpoints();

app.MapDefaultEndpoints();

app.Run();
