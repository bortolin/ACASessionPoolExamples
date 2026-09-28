using Azure.Core;
using Azure.Identity;

namespace DemoAIDynamicSession.ApiService.CodeExecution;

public static class CodeExecutionServiceExtensions
{
    public static IHostApplicationBuilder AddCodeExecution(this IHostApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(DynamicSessionsOptions.SectionName);

        builder.Services.AddOptions<DynamicSessionsOptions>().Bind(section);

        var options = new DynamicSessionsOptions();
        section.Bind(options);

        if (options.IsConfigured)
        {
            builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
            builder.Services
                .AddHttpClient<ICodeExecutor, DynamicSessionsCodeExecutor>(client =>
                    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds + 30));
        }
        else if (options.AllowLocalFallback && builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<ICodeExecutor, LocalPythonCodeExecutor>();
        }
        else
        {
            builder.Services.AddSingleton<ICodeExecutor, DisabledCodeExecutor>();
        }

        return builder;
    }
}
