using Microsoft.Extensions.AI;
using Balzor.Chat.Components.Models;

namespace Balzor.Chat.Components.Services;

/// <summary>
/// Motore di una conversazione, indipendente da Blazor e da un modello specifico: si appoggia
/// unicamente a <see cref="IChatClient"/> (Microsoft.Extensions.AI), quindi funziona con qualsiasi
/// provider/agente configurato dal progetto che consuma il componente (OpenAI, Azure OpenAI,
/// Ollama, un agente custom, ecc.).
/// </summary>
/// <remarks>
/// Espone lo stato della chat (messaggi, streaming, attivit&#224; in background) e notifica la UI
/// tramite l'evento <see cref="Changed"/>, cos&#236; da poter essere usato anche fuori da un componente
/// Razor (es. in test o in un altro contesto UI).
/// </remarks>
public sealed class AiChatConversation
{
    private readonly IChatClient _chatClient;
    private readonly List<ChatUiMessage> _messages = [];
    private CancellationTokenSource? _sendCts;

    public AiChatConversation(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }

    /// <summary>Istruzioni di sistema inviate al modello ma non mostrate nella cronologia della UI.</summary>
    public string? SystemInstructions { get; set; }

    /// <summary>Opzioni da inoltrare al modello (temperature, tools, ecc.). Facoltative.</summary>
    public ChatOptions? ChatOptions { get; set; }

    public IReadOnlyList<ChatUiMessage> Messages => _messages;

    public bool IsResponding { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Notifica un cambiamento di stato (nuovo testo, nuova attivit&#224;, fine risposta, ...).</summary>
    public event Action? Changed;

    public bool CanSend(string? userText) => !IsResponding && !string.IsNullOrWhiteSpace(userText);

    public Task SendAsync(string userText, CancellationToken cancellationToken) =>
        SendAsync(userText, attachments: null, cancellationToken);

    public async Task SendAsync(
        string userText,
        IReadOnlyList<ChatAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanSend(userText))
        {
            return;
        }

        LastError = null;
        _messages.Add(ChatUiMessage.FromRequest(ChatRole.User, userText.Trim(), attachments ?? []));

        var assistantMessage = new ChatUiMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = ChatRole.Assistant,
            IsStreaming = true,
        };
        _messages.Add(assistantMessage);

        IsResponding = true;
        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NotifyChanged();

        try
        {
            var history = BuildHistoryForModel();
            var pendingActivitiesByCallId = new Dictionary<string, string>();

            await foreach (var update in _chatClient.GetStreamingResponseAsync(history, ChatOptions, _sendCts.Token))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            assistantMessage.AppendText(text.Text);
                            break;

                        case FunctionCallContent call:
                            var activity = assistantMessage.StartActivity(
                                call.CallId,
                                title: $"Esecuzione di \"{call.Name}\"",
                                detail: FormatArguments(call.Arguments));
                            pendingActivitiesByCallId[call.CallId] = activity.Id;
                            break;

                        case FunctionResultContent result:
                            assistantMessage.CompleteActivity(
                                result.CallId,
                                detail: result.Result?.ToString(),
                                failed: result.Exception is not null);
                            break;
                    }
                }

                NotifyChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // Interruzione richiesta dall'utente: non è un errore da segnalare.
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            assistantMessage.Error = ex.Message;
        }
        finally
        {
            assistantMessage.IsStreaming = false;
            IsResponding = false;
            _sendCts?.Dispose();
            _sendCts = null;
            NotifyChanged();
        }
    }

    /// <summary>Interrompe la risposta in corso, se presente.</summary>
    public void CancelResponse() => _sendCts?.Cancel();

    public void ClearConversation()
    {
        CancelResponse();
        _messages.Clear();
        LastError = null;
        NotifyChanged();
    }

    private List<ChatMessage> BuildHistoryForModel()
    {
        List<ChatMessage> history = [];
        if (!string.IsNullOrWhiteSpace(SystemInstructions))
        {
            history.Add(new ChatMessage(ChatRole.System, SystemInstructions));
        }

        history.AddRange(_messages
            .Where(m => string.IsNullOrEmpty(m.Error))
            .Select(m => m.ToChatMessage()));

        return history;
    }

    private static string? FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        return string.Join(", ", arguments.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    private void NotifyChanged() => Changed?.Invoke();
}
