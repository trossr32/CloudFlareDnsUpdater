using CloudFlare.Client;
using CloudFlare.Client.Api.Authentication;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using CloudFlareDnsUpdater.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;
using System.Text.RegularExpressions;

namespace CloudFlareDnsUpdater.HostedServices;

internal partial class DnsUpdaterHostedService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IAuthentication _authentication;
    private readonly TimeSpan _updateInterval;
    private readonly string _limitToZoneByDomain;

    public DnsUpdaterHostedService(HttpClient httpClient, ILogger logger, IConfiguration config)
    {
        _httpClient = httpClient;
        _logger = logger.ForContext<DnsUpdaterHostedService>();

        var token = config.GetValue<string>("CloudFlare:ApiToken");
        var email = config.GetValue<string>("CloudFlare:Email");
        var key = config.GetValue<string>("CloudFlare:ApiKey");

        _authentication = !string.IsNullOrEmpty(token)
            ? new ApiTokenAuthentication(token)
            : new ApiKeyAuthentication(email, key);

        _updateInterval = TimeSpan.FromSeconds(config.GetValue("UpdateIntervalSeconds", 30));

        _limitToZoneByDomain = config.GetValue<string>("LimitToZoneByDomain");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Started DNS updater...");

        while (!cancellationToken.IsCancellationRequested)
        {
            await UpdateDnsAsync(cancellationToken);

            _logger.Debug("Finished update process. Waiting '{@UpdateInterval}' for next check", _updateInterval);

            await Task.Delay(_updateInterval, cancellationToken);
        }
    }

    private async Task UpdateDnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new CloudFlareClient(_authentication);
            var externalIpAddress = await GetIpAddressAsync(cancellationToken);

            _logger.Debug("Got ip from external provider: {IP}", externalIpAddress?.ToString());

            if (externalIpAddress is null)
            {
                _logger.Error("All external IP providers failed to resolve the IP");

                return;
            }

            var zones = (await client.Zones.GetAsync(cancellationToken: cancellationToken)).Result;

            _logger.Debug("Found the following zones : {@Zones}", zones.Select(x => x.Name));

            foreach (var zone in zones)
            {
                var records = (await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter {Type = DnsRecordType.A}, null, cancellationToken)).Result;

                _logger.Debug("Found the following 'A' records in zone '{Zone}': {@Records}", zone.Name, records.Select(x => x.Name));

                foreach (var record in records)
                {
                    // skip records that do not end with the domain we want to limit to if limiter is set
                    if (!string.IsNullOrWhiteSpace(_limitToZoneByDomain) && !record.Name.EndsWith(_limitToZoneByDomain))
                    {
                        _logger.Debug("Skipping record '{Record}' because it does not end with '{LimitToZoneByDomain}'", record.Name, _limitToZoneByDomain);

                        continue;
                    }

                    if (record.Type is not DnsRecordType.A || record.Content == externalIpAddress.ToString())
                    {
                        _logger.Debug("The IP for record '{Record}' in zone '{Zone}' is already '{ExternalIpAddress}'", record.Name, zone.Name, externalIpAddress.ToString());

                        continue;
                    }

                    var modified = new ModifiedDnsRecord
                    {
                        Type = DnsRecordType.A,
                        Name = record.Name,
                        Content = externalIpAddress.ToString(),
                    };

                    var updateResult = await client.Zones.DnsRecords.UpdateAsync(zone.Id, record.Id, modified, cancellationToken);

                    if (updateResult.Success)
                    {
                        _logger.Information("Successfully updated record '{Record}' ip from '{PreviousIp}' to '{ExternalIpAddress}' in zone '{Zone}'", record.Name, record.Content, externalIpAddress.ToString(), zone.Name);

                        continue;
                    }

                    _logger.Error("The following errors happened during update of record '{Record}' in zone '{Zone}': {@Error}", record.Name, zone.Name, updateResult.Errors);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected exception happened");
        }
    }

    private async Task<IPAddress> GetIpAddressAsync(CancellationToken cancellationToken)
    {
        IPAddress ipAddress = null;

        foreach (var provider in ExternalIpProviders.Providers)
        {
            if (ipAddress is not null)
                return ipAddress;

            var response = await _httpClient.GetAsync(provider, cancellationToken);

            if (!response.IsSuccessStatusCode)
                continue;

            var ip = await response.Content.ReadAsStringAsync(cancellationToken);

            UnwantedCharacters().Replace(ip, string.Empty);

            ipAddress = IPAddress.Parse(ip);
        }

        return ipAddress;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [GeneratedRegex(@"\t|\n|\r")]
    private static partial Regex UnwantedCharacters();
}