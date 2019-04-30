﻿using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CloudFlare.Client;
using CloudFlare.Client.Enumerators;
using CloudFlare.Client.Models;
using CloudFlareDnsUpdater.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudFlareDnsUpdater.HostedServices
{
    internal class DnsUpdaterHostedService : IHostedService, IDisposable
    {
        private readonly ILogger _logger;
        private readonly Authentication _auth;
        private Timer _timer;

        public DnsUpdaterHostedService(ILogger<DnsUpdaterHostedService> logger, Authentication auth)
        {
            _logger = logger;
            _auth = auth;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"[{DateTime.UtcNow} | Info] Started DNS updater...");


            _timer = new Timer(UpdateDns, null, TimeSpan.Zero,
                TimeSpan.FromSeconds(30));

            return Task.CompletedTask;
        }

        public void UpdateDns(object state)
        {
            try
            {
                using (var client = new CloudFlareClient(_auth))
                {
                    var externalIpAddress = GetIpAddress();
                    
                    // In case we don't have valid ip
                    if (string.IsNullOrEmpty(externalIpAddress))
                    {
                        _logger.LogError($"[{DateTime.UtcNow} | Error] External IP is null.");

                        return;
                    }

                    var zones = client.GetZonesAsync().Result;

                    foreach (var zone in zones.Result)
                    {
                        var records = client.GetDnsRecordsAsync(zone.Id, DnsRecordType.A).Result;
                        foreach (var record in records.Result)
                        {
                            if (record.Type == DnsRecordType.A && record.Content != externalIpAddress)
                            {
                                var updateResult = client.UpdateDnsRecordAsync(zone.Id, record.Id,
                                    DnsRecordType.A, record.Name, externalIpAddress).Result;

                                if (!updateResult.Success)
                                {
                                    foreach (var error in updateResult.Errors)
                                    {
                                        _logger.LogError($"[{DateTime.UtcNow} | Error] {{{record.Name}}} {error.Message}");
                                    }

                                }
                                else
                                {
                                    _logger.LogInformation(
                                        $"[{DateTime.UtcNow} | Update] {{{record.Name}}} {record.Content} -> {externalIpAddress}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogInformation(
                    $"[{DateTime.UtcNow} | Error] No internet connection.");
            }
        }

        private string GetIpAddress()
        {
            var ipAddress = "";
            foreach (var provider in ExternalIpProviders.Providers)
            {
                if (!string.IsNullOrEmpty(ipAddress))
                {
                    break;
                }

                ipAddress = GetIPAddressFromProvider(provider);
            }

            return Regex.Replace(ipAddress, @"\t|\n|\r", "");
        }

        private string GetIPAddressFromProvider(string providerUrl)
        {
            using (var client = new HttpClient())
            {
                var response = client.GetAsync(providerUrl).Result;
                if (response.IsSuccessStatusCode)
                {
                    return response.Content.ReadAsStringAsync().Result;
                }
            }

            return "";
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);

            return Task.CompletedTask;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer?.Dispose();
            }
        }

        public void Dispose()
        {
            // Dispose of unmanaged resources.
            Dispose(true);
            // Suppress finalization.
            GC.SuppressFinalize(this);
        }
    }
}
