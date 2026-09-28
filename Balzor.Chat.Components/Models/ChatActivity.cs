namespace Balzor.Chat.Components.Models;

/// <summary>
/// Rappresenta una singola attivit&#224; in background legata alla generazione di un messaggio
/// (chiamata a funzione/tool, step di un agente, ecc.), analoga alle "task" mostrate dagli
/// assistenti AI mentre lavorano dietro le quinte.
/// </summary>
public sealed class ChatActivity
{
    public required string Id { get; init; }

    /// <summary>Etichetta breve mostrata nella UI (es. nome del tool o titolo dello step).</summary>
    public required string Title { get; set; }

    /// <summary>Dettaglio opzionale (es. argomenti passati, esito, messaggio di errore).</summary>
    public string? Detail { get; set; }

    public ChatActivityStatus Status { get; set; } = ChatActivityStatus.Pending;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }
}
