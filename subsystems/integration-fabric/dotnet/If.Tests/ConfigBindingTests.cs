using If.MicrosoftGraph;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace If.Tests;

/// <summary>Verifies that MicrosoftGraphOptions binds correctly from configuration.</summary>
public sealed class ConfigBindingTests
{
    [Fact]
    public void MicrosoftGraphOptions_BindsFromInMemoryConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MicrosoftGraph:TenantId"]     = "test-tenant-id",
                ["MicrosoftGraph:ClientId"]     = "test-client-id",
                ["MicrosoftGraph:ClientSecret"] = "test-secret",
                ["MicrosoftGraph:RedirectUri"]  = "http://localhost:5050/auth/callback",
                ["MicrosoftGraph:Scopes:0"]     = "Calendars.Read",
                ["MicrosoftGraph:Scopes:1"]     = "Mail.Read",
                ["MicrosoftGraph:Scopes:2"]     = "User.Read",
                ["MicrosoftGraph:Scopes:3"]     = "offline_access",
            })
            .Build();

        var opts = config.GetSection("MicrosoftGraph").Get<MicrosoftGraphOptions>();

        Assert.NotNull(opts);
        Assert.Equal("test-tenant-id",                    opts.TenantId);
        Assert.Equal("test-client-id",                    opts.ClientId);
        Assert.Equal("test-secret",                       opts.ClientSecret);
        Assert.Equal("http://localhost:5050/auth/callback", opts.RedirectUri);
        Assert.Equal(4,                                   opts.Scopes.Count);
        Assert.Contains("Calendars.Read",                 opts.Scopes);
        Assert.Contains("offline_access",                 opts.Scopes);
    }

    [Fact]
    public void AddMicrosoftGraphIntegration_ThrowsWhenClientSecretMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MicrosoftGraph:TenantId"]     = "t",
                ["MicrosoftGraph:ClientId"]     = "c",
                ["MicrosoftGraph:ClientSecret"] = "",
                ["MicrosoftGraph:RedirectUri"]  = "http://localhost:5050/auth/callback",
                ["MicrosoftGraph:Scopes:0"]     = "User.Read",
            })
            .Build();

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddMicrosoftGraphIntegration(config));

        Assert.Contains("ClientSecret", ex.Message);
    }
}
