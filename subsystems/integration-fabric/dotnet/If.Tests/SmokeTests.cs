using If.Contracts;
using If.MicrosoftGraph;
using Xunit;

namespace If.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void MicrosoftGraphCalendarSource_CanBeInstantiated()
    {
        var opts = new MicrosoftGraphOptions
        {
            TenantId     = "fake-tenant",
            ClientId     = "fake-client",
            ClientSecret = "fake-secret",
            RedirectUri  = "http://localhost:5050/auth/callback",
            Scopes       = ["User.Read"],
        };
        var provider = new MicrosoftGraphTokenProvider(opts);
        var sut      = new MicrosoftGraphCalendarSource("fake-token", "fake-oid", provider, "fake-tenant");
        Assert.NotNull(sut);
    }

    [Fact]
    public void MicrosoftGraphMailSource_CanBeInstantiated()
    {
        var opts = new MicrosoftGraphOptions
        {
            TenantId     = "fake-tenant",
            ClientId     = "fake-client",
            ClientSecret = "fake-secret",
            RedirectUri  = "http://localhost:5050/auth/callback",
            Scopes       = ["User.Read"],
        };
        var provider = new MicrosoftGraphTokenProvider(opts);
        var sut      = new MicrosoftGraphMailSource("fake-token", "fake-oid", provider, "fake-tenant");
        Assert.NotNull(sut);
    }

    [Fact]
    public void GraphSyncCheckpointStore_CanBeInstantiated()
    {
        var sut = new GraphSyncCheckpointStore();
        Assert.NotNull(sut);
    }

    [Fact]
    public void MicrosoftGraphTokenProvider_CanBeInstantiated()
    {
        var opts = new MicrosoftGraphOptions
        {
            TenantId     = "fake-tenant",
            ClientId     = "fake-client",
            ClientSecret = "fake-secret",
            RedirectUri  = "http://localhost:5050/auth/callback",
            Scopes       = ["User.Read"],
        };
        var sut = new MicrosoftGraphTokenProvider(opts);
        Assert.NotNull(sut);
    }

    [Fact]
    public void GraphCalendarMapper_CanBeInstantiated()
    {
        var sut = new GraphCalendarMapper();
        Assert.NotNull(sut);
    }

    [Fact]
    public void GraphMailMapper_CanBeInstantiated()
    {
        var sut = new GraphMailMapper();
        Assert.NotNull(sut);
    }

    [Fact]
    public void GraphRetryPolicy_CanBeInstantiated()
    {
        var sut = new GraphRetryPolicy();
        Assert.NotNull(sut);
    }

    [Fact]
    public void MicrosoftGraphCalendarSource_Implements_ICalendarSource()
    {
        Assert.True(typeof(ICalendarSource).IsAssignableFrom(typeof(MicrosoftGraphCalendarSource)));
    }

    [Fact]
    public void MicrosoftGraphMailSource_Implements_IMailSource()
    {
        Assert.True(typeof(IMailSource).IsAssignableFrom(typeof(MicrosoftGraphMailSource)));
    }

    [Fact]
    public void GraphSyncCheckpointStore_Implements_IIntegrationCheckpointStore()
    {
        Assert.True(typeof(IIntegrationCheckpointStore).IsAssignableFrom(typeof(GraphSyncCheckpointStore)));
    }
}
