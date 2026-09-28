namespace Balzor.Chat.Components.Models;

/// <summary>Un prompt suggerito mostrato all'utente (es. in una chat vuota) come scorciatoia.</summary>
public sealed class SuggestedPrompt
{
    public required string Text { get; init; }

    /// <summary>Titolo breve mostrato sul chip/bottone. Se nullo viene usato <see cref="Text"/>.</summary>
    public string? Label { get; init; }

    /// <summary>Classe icona opzionale (es. Bootstrap Icons "bi bi-lightbulb").</summary>
    public string? IconCssClass { get; init; }
}
