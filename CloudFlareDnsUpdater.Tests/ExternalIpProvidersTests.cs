using CloudFlareDnsUpdater.Providers;
using Xunit;

namespace CloudFlareDnsUpdater.Tests;

public class ExternalIpProvidersTests
{
    [Fact]
    public void Providers_IsNotEmpty()
    {
        Assert.NotEmpty(ExternalIpProviders.Providers);
    }

    [Fact]
    public void Providers_AreAllValidAbsoluteHttpsUris()
    {
        foreach (var provider in ExternalIpProviders.Providers)
        {
            Assert.True(Uri.TryCreate(provider, UriKind.Absolute, out var uri), $"'{provider}' is not a valid absolute URI");
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        }
    }

    [Fact]
    public void Providers_ContainsNoDuplicates()
    {
        var providers = ExternalIpProviders.Providers.ToList();

        Assert.Equal(providers.Count, providers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
