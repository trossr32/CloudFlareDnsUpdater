namespace CloudFlareDnsUpdater.Providers;

public static class ExternalIpProviders
{
    public static IEnumerable<string> Providers { get; }

    static ExternalIpProviders()
    {
        Providers =
        [
            "https://ipecho.net/plain",
            "https://icanhazip.com/",
            "https://whatismyip.akamai.com",
            "https://tnx.nl/ip"
        ];
    }
}