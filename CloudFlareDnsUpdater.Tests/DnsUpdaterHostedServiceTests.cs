using System.Net;
using System.Reflection;
using CloudFlare.Client.Api.Authentication;
using CloudFlareDnsUpdater.HostedServices;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace CloudFlareDnsUpdater.Tests;

public class DnsUpdaterHostedServiceTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static IConfiguration BuildConfig(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static DnsUpdaterHostedService CreateService(Dictionary<string, string> config, HttpMessageHandler handler = null) =>
        new(new HttpClient(handler ?? new FakeHttpMessageHandler(_ => throw new InvalidOperationException("No HTTP calls expected"))), Logger, BuildConfig(config));

    private static T GetPrivateField<T>(object instance, string fieldName) =>
        (T)typeof(DnsUpdaterHostedService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    [Fact]
    public void Constructor_WithApiToken_UsesApiTokenAuthentication()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["CloudFlare:ApiToken"] = "test-token"
        });

        Assert.IsType<ApiTokenAuthentication>(GetPrivateField<IAuthentication>(service, "_authentication"));
    }

    [Fact]
    public void Constructor_WithEmailAndApiKey_UsesApiKeyAuthentication()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["CloudFlare:Email"] = "user@example.com",
            ["CloudFlare:ApiKey"] = "test-key"
        });

        Assert.IsType<ApiKeyAuthentication>(GetPrivateField<IAuthentication>(service, "_authentication"));
    }

    [Fact]
    public void Constructor_WithTokenAndKey_PrefersApiTokenAuthentication()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["CloudFlare:ApiToken"] = "test-token",
            ["CloudFlare:Email"] = "user@example.com",
            ["CloudFlare:ApiKey"] = "test-key"
        });

        Assert.IsType<ApiTokenAuthentication>(GetPrivateField<IAuthentication>(service, "_authentication"));
    }

    [Fact]
    public void Constructor_WithoutUpdateInterval_DefaultsToThirtySeconds()
    {
        var service = CreateService([]);

        Assert.Equal(TimeSpan.FromSeconds(30), GetPrivateField<TimeSpan>(service, "_updateInterval"));
    }

    [Fact]
    public void Constructor_WithUpdateInterval_UsesConfiguredValue()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["UpdateIntervalSeconds"] = "120"
        });

        Assert.Equal(TimeSpan.FromSeconds(120), GetPrivateField<TimeSpan>(service, "_updateInterval"));
    }

    [Fact]
    public void Constructor_WithLimitToZoneByDomain_UsesConfiguredValue()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["LimitToZoneByDomain"] = "example.com"
        });

        Assert.Equal("example.com", GetPrivateField<string>(service, "_limitToZoneByDomain"));
    }

    [Fact]
    public async Task GetIpAddressAsync_FirstProviderSucceeds_ReturnsParsedIp()
    {
        var service = CreateService([], new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1.2.3.4") }));

        var ip = await InvokeGetIpAddressAsync(service);

        Assert.Equal(IPAddress.Parse("1.2.3.4"), ip);
    }

    [Fact]
    public async Task GetIpAddressAsync_FirstProviderFails_FallsBackToNextProvider()
    {
        var callCount = 0;

        var service = CreateService([], new FakeHttpMessageHandler(_ =>
            ++callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("5.6.7.8") }));

        var ip = await InvokeGetIpAddressAsync(service);

        Assert.Equal(IPAddress.Parse("5.6.7.8"), ip);
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task GetIpAddressAsync_AllProvidersFail_ReturnsNull()
    {
        var service = CreateService([], new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var ip = await InvokeGetIpAddressAsync(service);

        Assert.Null(ip);
    }

    private static async Task<IPAddress> InvokeGetIpAddressAsync(DnsUpdaterHostedService service)
    {
        var method = typeof(DnsUpdaterHostedService)
            .GetMethod("GetIpAddressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        return await (Task<IPAddress>)method.Invoke(service, [CancellationToken.None]);
    }

    private class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
