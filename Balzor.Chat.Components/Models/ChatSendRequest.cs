namespace Balzor.Chat.Components.Models;

/// <summary>Testo e file associati a un invio della chat.</summary>
public sealed class ChatSendRequest
{
    public required string Text { get; init; }

    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = [];
}
