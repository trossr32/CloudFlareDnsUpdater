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
    public void Constructor_WithoutExcludeRecords_ExcludesNothing()
    {
        var service = CreateService([]);

        Assert.Empty(GetPrivateField<HashSet<string>>(service, "_excludeRecords"));
        Assert.False(service.IsExcluded("calitally.example.com"));
    }

    [Fact]
    public void Constructor_WithExcludeRecords_ParsesAndNormalisesEntries()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["ExcludeRecords"] = " Calitally.Example.com. , nas.example.com,, ;other.example.com "
        });

        var excluded = GetPrivateField<HashSet<string>>(service, "_excludeRecords");

        Assert.Equal(3, excluded.Count);
        Assert.True(service.IsExcluded("calitally.example.com"));
        Assert.True(service.IsExcluded("CALITALLY.EXAMPLE.COM."));
        Assert.True(service.IsExcluded("nas.example.com"));
        Assert.True(service.IsExcluded("other.example.com"));
        Assert.False(service.IsExcluded("example.com"));
        Assert.False(service.IsExcluded("www.calitally.example.com"));
        Assert.False(service.IsExcluded(null));
    }

    [Fact]
    public void Constructor_WithoutSkipPrivateIpRecords_DefaultsToTrue()
    {
        var service = CreateService([]);

        Assert.True(GetPrivateField<bool>(service, "_skipPrivateIpRecords"));
    }

    [Fact]
    public void Constructor_WithSkipPrivateIpRecordsFalse_UsesConfiguredValue()
    {
        var service = CreateService(new Dictionary<string, string>
        {
            ["SkipPrivateIpRecords"] = "false"
        });

        Assert.False(GetPrivateField<bool>(service, "_skipPrivateIpRecords"));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.1")]
    [InlineData("192.168.1.50")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.10")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    public void IsPrivateIp_PrivateAddresses_ReturnsTrue(string content) =>
        Assert.True(DnsUpdaterHostedService.IsPrivateIp(content));

    [Theory]
    [InlineData("176.249.90.140")]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    [InlineData(null)]
    public void IsPrivateIp_PublicOrInvalidContent_ReturnsFalse(string content) =>
        Assert.False(DnsUpdaterHostedService.IsPrivateIp(content));

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
