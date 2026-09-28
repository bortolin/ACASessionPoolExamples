using Azure.Core;
using Azure.Identity;
using DemoAIDynamicSession.ApiService.CodeExecution;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DemoAIDynamicSession.ApiService.SessionManagement;

public static class SessionManagementEndpoints
{
    private const string KeepAliveCode = "pass";

    private const string CreateSessionCode = """
        import platform, datetime
        print(f"Sessione avviata su Python {platform.python_version()} alle {datetime.datetime.utcnow():%H:%M:%S} UTC")
        """;

    public static IHostApplicationBuilder AddSessionManagement(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<SessionManagementOptions>()
            .Bind(builder.Configuration.GetSection(DynamicSessionsOptions.SectionName));

        builder.Services.TryAddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        builder.Services.AddHttpClient<DynamicSessionsManagementClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(60));

        return builder;
    }

    public static IEndpointRouteBuilder MapSessionManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/sessions")
            .WithTags("SessionManagement");

        group.MapGet("/status", (IOptions<SessionManagementOptions> options) =>
                Results.Ok(BuildStatus(options.Value)))
            .WithName("GetSessionManagementStatus");

        group.MapGet("/", async (
            IOptions<SessionManagementOptions> options,
            DynamicSessionsManagementClient client,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.IsConfigured)
            {
                return NotConfigured();
            }

            return await RunAsync("Lettura sessioni fallita", async () =>
                Results.Ok(await client.ListSessionsAsync(cancellationToken)));
        })
        .WithName("ListSessions");

        group.MapGet("/{identifier}", async (
            string identifier,
            bool? includeFiles,
            IOptions<SessionManagementOptions> options,
            DynamicSessionsManagementClient client,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.IsConfigured)
            {
                return NotConfigured();
            }

            if (ValidateIdentifier(identifier) is { } invalid)
            {
                return invalid;
            }

            return await RunAsync("Lettura sessione fallita", async () =>
            {
                var session = await client.GetSessionAsync(identifier.Trim(), cancellationToken);
                if (session is null)
                {
                    return SessionNotFound(identifier);
                }

                // La lista file viene letta solo su richiesta esplicita: ogni chiamata alla
                // sessione azzera il cooldown e ne prolunga la durata.
                IReadOnlyList<SessionFile>? files = null;
                if (includeFiles == true && services.GetService<ICodeExecutor>() is { Mode: CodeExecutorModes.AzureDynamicSessions } executor)
                {
                    files = await executor.ListFilesAsync(session.Identifier, cancellationToken);
                    session = await client.GetSessionAsync(session.Identifier, cancellationToken) ?? session;
                }

                return Results.Ok(new SessionDetails(session, files));
            });
        })
        .WithName("GetSession");

        group.MapDelete("/{identifier}", async (
            string identifier,
            IOptions<SessionManagementOptions> options,
            DynamicSessionsManagementClient client,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.IsConfigured)
            {
                return NotConfigured();
            }

            if (ValidateIdentifier(identifier) is { } invalid)
            {
                return invalid;
            }

            return await RunAsync("Eliminazione sessione fallita", async () =>
                await client.DeleteSessionAsync(identifier.Trim(), cancellationToken)
                    ? Results.NoContent()
                    : SessionNotFound(identifier));
        })
        .WithName("DeleteSession");

        group.MapPost("/", async (
            CreateSessionRequest? request,
            IOptions<SessionManagementOptions> options,
            DynamicSessionsManagementClient client,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.IsConfigured)
            {
                return NotConfigured();
            }

            var identifier = string.IsNullOrWhiteSpace(request?.Identifier)
                ? $"mgmt-{Guid.NewGuid():n}"[..16]
                : request.Identifier.Trim();

            if (ValidateIdentifier(identifier) is { } invalid)
            {
                return invalid;
            }

            // Le sessioni vengono allocate alla prima richiesta con un nuovo identificatore:
            // si esegue un piccolo snippet per "accendere" la sandbox.
            return await ExecuteAndReturnSessionAsync(identifier, CreateSessionCode, client, services, cancellationToken);
        })
        .WithName("CreateSession");

        group.MapPost("/{identifier}/keep-alive", async (
            string identifier,
            IOptions<SessionManagementOptions> options,
            DynamicSessionsManagementClient client,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.IsConfigured)
            {
                return NotConfigured();
            }

            if (ValidateIdentifier(identifier) is { } invalid)
            {
                return invalid;
            }

            return await RunAsync("Prolungamento sessione fallito", async () =>
            {
                // Evita di ricreare una sessione già terminata.
                if (await client.GetSessionAsync(identifier.Trim(), cancellationToken) is null)
                {
                    return SessionNotFound(identifier);
                }

                return await ExecuteAndReturnSessionAsync(identifier.Trim(), KeepAliveCode, client, services, cancellationToken);
            });
        })
        .WithName("KeepAliveSession");

        return endpoints;
    }

    private static async Task<IResult> ExecuteAndReturnSessionAsync(
        string identifier,
        string code,
        DynamicSessionsManagementClient client,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (services.GetService<ICodeExecutor>() is not { Mode: CodeExecutorModes.AzureDynamicSessions } executor)
        {
            return NotConfigured();
        }

        return await RunAsync("Esecuzione nella sessione fallita", async () =>
        {
            var before = await client.GetSessionAsync(identifier, cancellationToken);
            await executor.ExecuteAsync(code, identifier, cancellationToken);

            // Il pool aggiorna lastAccessedAt/expireAt con qualche secondo di ritardo:
            // si attende brevemente che i metadati riflettano la nuova attività.
            var session = await client.GetSessionAsync(identifier, cancellationToken);
            for (var attempt = 0; attempt < 8 && before is not null &&
                 session?.LastAccessedAt == before.LastAccessedAt; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                session = await client.GetSessionAsync(identifier, cancellationToken);
            }

            return Results.Ok(session ?? new SessionInfo(identifier, null, null, null, null));
        });
    }

    private static async Task<IResult> RunAsync(string title, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (CodeExecutionException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: title);
        }
        catch (Azure.Identity.AuthenticationFailedException ex)
        {
            return Results.Problem(
                detail: $"Impossibile ottenere un token per https://dynamicsessions.io: {ex.Message}",
                statusCode: StatusCodes.Status502BadGateway,
                title: title);
        }
    }

    private static IResult NotConfigured() =>
        Results.Problem(
            detail: "La gestione delle sessioni richiede un session pool Azure. " +
                    "Configura 'DynamicSessions:PoolManagementEndpoint'.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Gestione sessioni non disponibile");

    private static IResult SessionNotFound(string identifier) =>
        Results.Problem(
            detail: $"La sessione '{identifier}' non esiste o è già terminata.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Sessione non trovata");

    private static IResult? ValidateIdentifier(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return Results.BadRequest("L'identificatore di sessione è obbligatorio.");
        }

        return identifier.Trim().Length <= 128
            ? null
            : Results.BadRequest("L'identificatore di sessione non può superare 128 caratteri.");
    }

    private static SessionManagementStatus BuildStatus(SessionManagementOptions options)
    {
        string? poolName = null;
        string? region = null;

        if (Uri.TryCreate(options.PoolManagementEndpoint, UriKind.Absolute, out var uri))
        {
            region = uri.Host.Split('.')[0];
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            var poolIndex = Array.FindIndex(segments, segment =>
                segment.Equals("sessionPools", StringComparison.OrdinalIgnoreCase));
            poolName = poolIndex >= 0 && poolIndex + 1 < segments.Length ? segments[poolIndex + 1] : null;
        }

        return new SessionManagementStatus(
            options.IsConfigured,
            options.PoolManagementEndpoint,
            poolName,
            region,
            options.ManagementApiVersion,
            options.IsConfigured
                ? "Le operazioni vengono inviate alla management API del session pool Azure."
                : "Session pool non configurato: la gestione sessioni è disponibile solo con Azure Container Apps dynamic sessions.");
    }
}
