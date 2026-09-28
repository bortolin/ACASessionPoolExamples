namespace Balzor.Chat.Components.Models;

/// <summary>File caricato dall'utente e inviato al provider AI come contenuto del messaggio.</summary>
public sealed class ChatAttachment
{
    public required string FileName { get; init; }

    public required string MediaType { get; init; }

    public required byte[] Data { get; init; }

    public long Length => Data.LongLength;
}
