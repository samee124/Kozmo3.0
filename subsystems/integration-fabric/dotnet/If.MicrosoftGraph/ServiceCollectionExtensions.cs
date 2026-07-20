using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace If.MicrosoftGraph;

/// <summary>DI registration helpers for Microsoft Graph integration services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MicrosoftGraphOptions and MicrosoftGraphTokenProvider from the
    /// "MicrosoftGraph" configuration section. Throws at startup if ClientSecret is absent.
    /// </summary>
    public static IServiceCollection AddMicrosoftGraphIntegration(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        var options = configuration.GetSection("MicrosoftGraph").Get<MicrosoftGraphOptions>()
            ?? throw new InvalidOperationException(
                "MicrosoftGraph configuration section is missing. " +
                "Ensure appsettings.json contains a 'MicrosoftGraph' section with TenantId and ClientId.");

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new InvalidOperationException(
                "MicrosoftGraph:ClientSecret is not set. " +
                "Run: dotnet user-secrets set \"MicrosoftGraph:ClientSecret\" \"<value>\" " +
                "--project tools/GraphAuthHarness");

        services.AddSingleton(options);
        services.AddSingleton<MicrosoftGraphTokenProvider>();

        return services;
    }
}
