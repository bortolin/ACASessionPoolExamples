using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;

namespace DemoAIDynamicSession.ApiService.Chat;

public static class DemoChatEndpoints
{
    private const int MaxMessages = 40;
    private const int MaxMessageLength = 8_000;

    public static IEndpointRouteBuilder MapDemoChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/chat")
            .WithTags("Chat");

        group.MapPost("/demo", async (
            DemoChatRequest request,
            IChatClient chatClient,
            CancellationToken cancellationToken) =>
        {
            var validationResult = Validate(request);
            if (validationResult is not null)
            {
                return validationResult;
            }

            var messages = request.Messages
                .Select(message => new ChatMessage(ToChatRole(message.Role), message.Text.Trim()))
                .ToArray();

            try
            {
                var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
                return Results.Ok(new DemoChatResponse(response.Text));
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
                    title: "Errore durante la risposta del modello");
            }
        })
        .WithName("RunDemoChat");

        return endpoints;
    }

    private static IResult? Validate(DemoChatRequest request)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, true))
        {
            return ToValidationProblem(results);
        }

        if (request.Messages.Count is 0 or > MaxMessages)
        {
            return Results.BadRequest($"La richiesta deve contenere tra 1 e {MaxMessages} messaggi.");
        }

        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (string.IsNullOrWhiteSpace(message.Text))
            {
                return Results.BadRequest($"Il messaggio in posizione {index} non può essere vuoto.");
            }

            if (message.Text.Length > MaxMessageLength)
            {
                return Results.BadRequest(
                    $"Il messaggio in posizione {index} non può superare {MaxMessageLength} caratteri.");
            }
        }

        return null;
    }

    private static IResult ToValidationProblem(IEnumerable<ValidationResult> results)
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

    private static ChatRole ToChatRole(string role)
    {
        var normalizedRole = role.Trim().ToLowerInvariant();
        return normalizedRole switch
        {
            "system" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "user" => ChatRole.User,
            "tool" => ChatRole.Tool,
            _ => new ChatRole(normalizedRole),
        };
    }
}

public sealed record DemoChatRequest(
    [property: Required] IReadOnlyList<DemoChatMessage> Messages);

public sealed record DemoChatMessage(
    [property: Required] string Role,
    [property: Required] string Text);

public sealed record DemoChatResponse(string Reply);
