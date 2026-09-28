namespace Balzor.Chat.Components.Models;

/// <summary>
/// Stato di un'attivit&#224; in background eseguita durante la generazione di una risposta
/// (es. chiamata a un tool/funzione, ricerca, esecuzione di codice, invocazione di un agente).
/// </summary>
public enum ChatActivityStatus
{
    Pending,
    Running,
    Completed,
    Failed,
}
