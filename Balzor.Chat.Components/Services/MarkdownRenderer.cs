using Markdig;

namespace Balzor.Chat.Components.Services;

/// <summary>
/// Converte il testo (markdown) restituito dal modello in HTML sicuro da inserire nella pagina.
/// L'HTML "raw" eventualmente presente nel markdown viene disabilitato: il testo prodotto da un
/// LLM non è mai attendibile quanto il codice dell'applicazione, quindi va trattato come input
/// potenzialmente ostile (prompt injection che tenta di iniettare script/markup).
/// </summary>
internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToSafeHtml(string? markdown) =>
        string.IsNullOrEmpty(markdown) ? string.Empty : Markdown.ToHtml(markdown, Pipeline);
}
