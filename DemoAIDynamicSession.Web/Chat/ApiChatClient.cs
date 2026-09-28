using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace DemoAIDynamicSession.Web.Chat;

public sealed class ApiChatClient(HttpClient httpClient) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var reply = await SendAsync(messages, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var reply = await SendAsync(messages, cancellationToken);
        foreach (var chunk in SplitIntoChunks(reply, 32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private async Task<string> SendAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var request = new DemoChatRequest(
            messages
                .Where(message => !string.IsNullOrWhiteSpace(message.Text))
                .Select(message => new DemoChatMessage(message.Role.Value, message.Text))
                .ToArray());

        using var response = await httpClient.PostAsJsonAsync("/api/chat/demo", request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadProblemAsync(response, cancellationToken);
            throw new InvalidOperationException(problem);
        }

        var result = await response.Content.ReadFromJsonAsync<DemoChatResponse>(cancellationToken);
        return result?.Reply
            ?? throw new InvalidOperationException("La API non ha restituito una risposta chat valida.");
    }

    private static async Task<string> ReadProblemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content
                .ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(cancellationToken);

            if (!string.IsNullOrWhiteSpace(problem?.Detail))
            {
                return problem.Detail;
            }

            if (!string.IsNullOrWhiteSpace(problem?.Title))
            {
                return problem.Title;
            }
        }
        catch (Exception)
        {
            // Risposta non conforme a ProblemDetails: si usa il messaggio generico.
        }

        return $"La API chat ha risposto con stato {(int)response.StatusCode} ({response.ReasonPhrase}).";
    }

    private static IEnumerable<string> SplitIntoChunks(string text, int chunkSize)
    {
        for (var index = 0; index < text.Length; index += chunkSize)
        {
            yield return text[index..Math.Min(index + chunkSize, text.Length)];
        }
    }
}

public sealed record DemoChatRequest(IReadOnlyList<DemoChatMessage> Messages);

public sealed record DemoChatMessage(string Role, string Text);

public sealed record DemoChatResponse(string Reply);
