using DemoAIDynamicSession.Web;
using DemoAIDynamicSession.Web.Chat;
using DemoAIDynamicSession.Web.CodeExecution;
using DemoAIDynamicSession.Web.CodeGeneration;
using DemoAIDynamicSession.Web.Components;
using DemoAIDynamicSession.Web.SemanticKernelDemo;
using DemoAIDynamicSession.Web.SessionManagement;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddOutputCache();

var apiServiceBaseAddress = new Uri("https+http://apiservice");

builder.Services.AddHttpClient<ApiChatClient>(client =>
{
    client.BaseAddress = apiServiceBaseAddress;
    client.Timeout = TimeSpan.FromMinutes(4);
});
builder.Services.AddScoped<IChatClient>(services => services.GetRequiredService<ApiChatClient>());

builder.Services.AddHttpClient<PythonCodeGenerationApiClient>(client =>
{
    client.BaseAddress = apiServiceBaseAddress;
    client.Timeout = TimeSpan.FromMinutes(4);
});
builder.Services.AddScoped<PythonCodeGenerationChatClient>();

builder.Services.AddHttpClient<CodeExecutionApiClient>(client =>
{
    client.BaseAddress = apiServiceBaseAddress;
    client.Timeout = TimeSpan.FromMinutes(4);
});

builder.Services.AddHttpClient<SemanticKernelDemoApiClient>(client =>
{
    client.BaseAddress = apiServiceBaseAddress;
    // Il flusso ora è agentico (più turni di ragionamento/tool-calling): può richiedere più tempo
    // di una singola esecuzione fissa, specie con un modello locale.
    client.Timeout = TimeSpan.FromMinutes(15);
});

builder.Services.AddHttpClient<SessionManagementApiClient>(client =>
{
    client.BaseAddress = apiServiceBaseAddress;
    client.Timeout = TimeSpan.FromMinutes(2);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

app.UseOutputCache();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapDefaultEndpoints();

app.MapGet("/files/{sessionId}/{fileName}", async (
    string sessionId,
    string fileName,
    CodeExecutionApiClient codeApi,
    CancellationToken cancellationToken) =>
{
    try
    {
        var (content, contentType) = await codeApi.DownloadFileAsync(sessionId, fileName, cancellationToken);
        return Results.File(content, contentType, fileName);
    }
    catch (CodeExecutionFailedException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Download fallito");
    }
})
.WithName("DownloadSessionFileProxy");

app.MapGet("/semantic-kernel-demo/files/{operationId}", async (
    string operationId,
    SemanticKernelDemoApiClient demoApi,
    CancellationToken cancellationToken) =>
{
    try
    {
        var (content, contentType, fileName) = await demoApi.DownloadAsync(operationId, cancellationToken);
        return Results.File(content, contentType, fileName);
    }
    catch (SemanticKernelDemoException ex)
    {
        return Results.Problem(
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway,
            title: "Download della demo fallito");
    }
})
.WithName("DownloadSemanticKernelDemoFileProxy");

app.Run();
