using CloudFlare.Client;
using CloudFlare.Client.Api.Authentication;
using CloudFlare.Client.Api.Result;
using CloudFlare.Client.Api.Zones.DnsRecord;
using CloudFlare.Client.Enumerators;
using CloudFlareDnsUpdater.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace CloudFlareDnsUpdater.HostedServices;

internal partial class DnsUpdaterHostedService : IHostedService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IAuthentication _authentication;
    private readonly TimeSpan _updateInterval;
    private readonly string _limitToZoneByDomain;
    private readonly HashSet<string> _excludeRecords;
    private readonly bool _skipPrivateIpRecords;

    public DnsUpdaterHostedService(HttpClient httpClient, ILogger logger, IConfiguration config)
    {
        _httpClient = httpClient;
        _logger = logger.ForContext<DnsUpdaterHostedService>();

        var token = config.GetValue<string>("CloudFlare:ApiToken");
        var email = config.GetValue<string>("CloudFlare:Email");
        var key = config.GetValue<string>("CloudFlare:ApiKey");

        _authentication = !string.IsNullOrEmpty(token)
            ? new ApiTokenAuthentication(token)
            : !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(key)
                ? new ApiKeyAuthentication(email, key)
                : null;

        _updateInterval = TimeSpan.FromSeconds(config.GetValue("UpdateIntervalSeconds", 30));

        _limitToZoneByDomain = config.GetValue<string>("LimitToZoneByDomain");

        _excludeRecords = ParseExcludeRecords(config.GetValue<string>("ExcludeRecords"));

        _skipPrivateIpRecords = config.GetValue("SkipPrivateIpRecords", true);
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
            if (_authentication is null)
            {
                _logger.Error("No CloudFlare credentials configured. Set CloudFlare:ApiToken or CloudFlare:Email and CloudFlare:ApiKey");

                return;
            }

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
                // skip zones that do not match the domain we want to limit to if limiter is set
                if (!string.IsNullOrWhiteSpace(_limitToZoneByDomain) && !zone.Name.EndsWith(_limitToZoneByDomain))
                {
                    _logger.Debug("Skipping zone '{Zone}' because it does not end with '{LimitToZoneByDomain}'", zone.Name, _limitToZoneByDomain);

                    continue;
                }

                CloudFlareResult<IReadOnlyList<DnsRecord>> recordsResult;

                try
                {
                    recordsResult = await client.Zones.DnsRecords.GetAsync(zone.Id, new DnsRecordFilter {Type = DnsRecordType.A}, null, cancellationToken);
                }
                catch (AuthenticationException)
                {
                    _logger.Warning("Unable to get DNS records for zone '{Zone}', the API token does not have DNS permissions for this zone", zone.Name);

                    continue;
                }

                if (!recordsResult.Success)
                {
                    _logger.Warning("Unable to get DNS records for zone '{Zone}', the API token may not have DNS permissions for this zone: {@Error}", zone.Name, recordsResult.Errors);

                    continue;
                }

                var records = recordsResult.Result;

                _logger.Debug("Found the following 'A' records in zone '{Zone}': {@Records}", zone.Name, records.Select(x => x.Name));

                foreach (var record in records)
                {
                    // skip records that do not end with the domain we want to limit to if limiter is set
                    if (!string.IsNullOrWhiteSpace(_limitToZoneByDomain) && !record.Name.EndsWith(_limitToZoneByDomain))
                    {
                        _logger.Debug("Skipping record '{Record}' because it does not end with '{LimitToZoneByDomain}'", record.Name, _limitToZoneByDomain);

                        continue;
                    }

                    // skip records explicitly excluded by name, e.g. a LAN-only host that must keep its private address
                    if (IsExcluded(record.Name))
                    {
                        _logger.Information("Skipping record '{Record}' in zone '{Zone}' because it is listed in ExcludeRecords", record.Name, zone.Name);

                        continue;
                    }

                    // skip records that point at a private address; those are never meant to track the external ip
                    if (_skipPrivateIpRecords && IsPrivateIp(record.Content))
                    {
                        _logger.Information("Skipping record '{Record}' in zone '{Zone}' because it points at the private address '{Content}' (set SkipPrivateIpRecords=false to update it anyway)", record.Name, zone.Name, record.Content);

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

    /// <summary>
    /// Parses the comma-separated ExcludeRecords setting into a set of normalised record names.
    /// Entries are trimmed, compared case-insensitively and stripped of a trailing dot, so "Calitally.Example.com." matches "calitally.example.com".
    /// </summary>
    internal static HashSet<string> ParseExcludeRecords(string excludeRecords)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(excludeRecords))
            return set;

        foreach (var entry in excludeRecords.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = entry.TrimEnd('.');

            if (name.Length > 0)
                set.Add(name);
        }

        return set;
    }

    /// <summary>
    /// True when the record name is listed in ExcludeRecords (case-insensitive, ignoring a trailing dot).
    /// </summary>
    internal bool IsExcluded(string recordName) =>
        !string.IsNullOrWhiteSpace(recordName) && _excludeRecords.Contains(recordName.Trim().TrimEnd('.'));

    /// <summary>
    /// True when the content is an IPv4 address that cannot be reached from the internet:
    /// RFC 1918 (10/8, 172.16/12, 192.168/16), loopback (127/8), link-local (169.254/16) or carrier-grade NAT (100.64/10).
    /// </summary>
    internal static bool IsPrivateIp(string content)
    {
        if (!IPAddress.TryParse(content, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var b = ip.GetAddressBytes();

        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    [GeneratedRegex(@"\t|\n|\r")]
    private static partial Regex UnwantedCharacters();
}