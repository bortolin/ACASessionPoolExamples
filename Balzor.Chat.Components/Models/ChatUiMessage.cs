using System.Text;
using Microsoft.Extensions.AI;

namespace Balzor.Chat.Components.Models;

/// <summary>
/// Messaggio cos&#236; come viene mostrato nella UI: aggiunge allo stato "puro" del modello
/// (ruolo + testo, compatibili con <see cref="ChatMessage"/> di Microsoft.Extensions.AI)
/// le informazioni necessarie a una chat, come streaming in corso, errori e attivit&#224;
/// in background associate alla risposta.
/// </summary>
public sealed class ChatUiMessage
{
    private readonly StringBuilder _text = new();

    public required string Id { get; init; }

    public required ChatRole Role { get; init; }

    public string Text => _text.ToString();

    public bool IsStreaming { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public List<ChatAttachment> Attachments { get; } = [];

    /// <summary>Attivit&#224; in background (chiamate a tool, step di agenti, ...) generate mentre questo messaggio veniva prodotto.</summary>
    public List<ChatActivity> Activities { get; } = [];

    public static ChatUiMessage FromText(ChatRole role, string text) => new ChatUiMessage
    {
        Id = Guid.NewGuid().ToString("N"),
        Role = role,
    }.AppendText(text);

    public static ChatUiMessage FromRequest(ChatRole role, string text, IEnumerable<ChatAttachment> attachments)
    {
        var message = FromText(role, text);
        message.Attachments.AddRange(attachments);
        return message;
    }

    public ChatUiMessage AppendText(string delta)
    {
        _text.Append(delta);
        return this;
    }

    public ChatActivity StartActivity(string id, string title, string? detail = null)
    {
        var activity = new ChatActivity { Id = id, Title = title, Detail = detail, Status = ChatActivityStatus.Running };
        Activities.Add(activity);
        return activity;
    }

    public ChatActivity? CompleteActivity(string id, string? detail = null, bool failed = false)
    {
        var activity = Activities.FirstOrDefault(a => a.Id == id);
        if (activity is null)
        {
            return null;
        }

        activity.Status = failed ? ChatActivityStatus.Failed : ChatActivityStatus.Completed;
        activity.CompletedAt = DateTimeOffset.Now;
        if (detail is not null)
        {
            activity.Detail = detail;
        }

        return activity;
    }

    /// <summary>Converte il messaggio nel tipo usato da Microsoft.Extensions.AI per rimandarlo al modello come storico.</summary>
    public ChatMessage ToChatMessage()
    {
        List<AIContent> contents = [new TextContent(Text)];
        contents.AddRange(Attachments.Select(attachment =>
            new DataContent(attachment.Data, attachment.MediaType) { Name = attachment.FileName }));
        return new ChatMessage(Role, contents);
    }
}
