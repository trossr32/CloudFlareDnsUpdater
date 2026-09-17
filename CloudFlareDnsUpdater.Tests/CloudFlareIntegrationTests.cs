using CloudFlare.Client;
using CloudFlare.Client.Api.Authentication;
using Xunit;

namespace CloudFlareDnsUpdater.Tests;

public class CloudFlareIntegrationTests
{
    // Enter a real Cloudflare API token here when you want to run this test.
    // Leave as an empty string to keep the test skipped.
    private const string ApiToken = "";

    [Fact]
    public async Task GetZones_WithApiToken_ReturnsSuccess()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(ApiToken), "No Cloudflare API token provided. Set the ApiToken constant to run this integration test.");

        using var client = new CloudFlareClient(new ApiTokenAuthentication(ApiToken));

        var result = await client.Zones.GetAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Cloudflare call failed: {string.Join(", ", result.Errors?.Select(e => e.Message) ?? [])}");
        Assert.NotNull(result.Result);
    }
}
