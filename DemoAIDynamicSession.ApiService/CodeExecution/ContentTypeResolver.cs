namespace DemoAIDynamicSession.ApiService.CodeExecution;

internal static class ContentTypeResolver
{
    public static string FromFileName(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".html" or ".htm" => "text/html",
            _ => "application/octet-stream",
        };
}
