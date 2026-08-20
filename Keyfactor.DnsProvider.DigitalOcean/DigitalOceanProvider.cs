// Copyright 2026 Keyfactor
// Licensed under the Apache License, Version 2.0
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keyfactor.Logging;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.DomainValidator.DigitalOcean
{
    internal class DigitalOceanProvider
    {
        private static readonly ILogger _logger = LogHandler.GetClassLogger<DigitalOceanProvider>();

        private readonly HttpClient _httpClient;

        private class DomainData
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }
        }

        private class DomainsResponse
        {
            [JsonPropertyName("domains")]
            public DomainData[] Domains { get; set; }

            [JsonPropertyName("links")]
            public LinksData Links { get; set; }
        }

        private class LinksData
        {
            [JsonPropertyName("pages")]
            public PagesData Pages { get; set; }
        }

        private class PagesData
        {
            [JsonPropertyName("next")]
            public string Next { get; set; }
        }

        private class RecordData
        {
            [JsonPropertyName("id")]
            public long Id { get; set; }

            [JsonPropertyName("type")]
            public string Type { get; set; }

            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("data")]
            public string Data { get; set; }

            [JsonPropertyName("ttl")]
            public int Ttl { get; set; }
        }

        private class RecordsResponse
        {
            [JsonPropertyName("domain_records")]
            public RecordData[] DomainRecords { get; set; }
        }

        private class CreateRecordResponse
        {
            [JsonPropertyName("domain_record")]
            public RecordData DomainRecord { get; set; }
        }

        public DigitalOceanProvider(string apiToken)
            : this(apiToken, new HttpClientHandler())
        {
        }

        // Internal constructor to allow unit tests to inject a fake HttpMessageHandler.
        internal DigitalOceanProvider(string apiToken, HttpMessageHandler handler)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                throw new ArgumentException("apiToken must not be empty", nameof(apiToken));
            }

            _httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.digitalocean.com/v2/")
            };

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<bool> CreateRecordAsync(string recordName, string value, string recordType)
        {
            _logger.LogDebug("Creating {RecordType} record for {RecordName}", recordType, recordName);

            var zone = await FindZoneForRecordAsync(recordName);
            var relativeName = RelativeRecordName(zone, recordName);

            var payload = new { type = recordType, name = relativeName, data = value, ttl = 300 };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"domains/{zone}/records", content);
            var result = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "DigitalOcean API rejected creation of {RecordType} record '{RelativeName}' in zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    recordType, relativeName, zone, (int)response.StatusCode, result);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)response.StatusCode} ({response.StatusCode}) creating {recordType} record '{relativeName}' in zone '{zone}': {result}");
            }

            _logger.LogInformation(
                "Created {RecordType} record '{RelativeName}' in DigitalOcean zone '{Zone}'",
                recordType, relativeName, zone);
            return true;
        }

        public async Task<bool> DeleteRecordAsync(string recordName, string recordType)
        {
            _logger.LogDebug("Deleting {RecordType} record for {RecordName}", recordType, recordName);

            var zone = await FindZoneForRecordAsync(recordName);
            var relativeName = RelativeRecordName(zone, recordName);

            var recordsResp = await _httpClient.GetAsync($"domains/{zone}/records?type={recordType}");
            var recordsBody = await recordsResp.Content.ReadAsStringAsync();
            if (!recordsResp.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "DigitalOcean API failed to list records for zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    zone, (int)recordsResp.StatusCode, recordsBody);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)recordsResp.StatusCode} ({recordsResp.StatusCode}) listing records in zone '{zone}': {recordsBody}");
            }

            var records = JsonSerializer.Deserialize<RecordsResponse>(recordsBody)?.DomainRecords ?? Array.Empty<RecordData>();
            var match = records.FirstOrDefault(r =>
                string.Equals(r.Type, recordType, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Name, relativeName, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                // Nothing to clean up — treat as success so cleanup is idempotent.
                _logger.LogInformation(
                    "No {RecordType} record '{RelativeName}' found in zone '{Zone}' to delete; treating cleanup as complete",
                    recordType, relativeName, zone);
                return true;
            }

            var deleteResp = await _httpClient.DeleteAsync($"domains/{zone}/records/{match.Id}");

            if (!deleteResp.IsSuccessStatusCode)
            {
                var deleteBody = await deleteResp.Content.ReadAsStringAsync();
                _logger.LogError(
                    "DigitalOcean API rejected deletion of {RecordType} record '{RelativeName}' ({RecordId}) in zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    recordType, relativeName, match.Id, zone, (int)deleteResp.StatusCode, deleteBody);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)deleteResp.StatusCode} ({deleteResp.StatusCode}) deleting {recordType} record '{relativeName}' in zone '{zone}': {deleteBody}");
            }

            _logger.LogInformation(
                "Deleted {RecordType} record '{RelativeName}' in DigitalOcean zone '{Zone}'",
                recordType, relativeName, zone);
            return true;
        }

        /// <summary>
        /// Fetches all domains (zones) on the account, paging through `links.pages.next`, and
        /// resolves the zone that owns the given record by longest matching name suffix, e.g.
        /// for "_acme-challenge.www.example.com" it tries "www.example.com", then "example.com".
        /// </summary>
        private async Task<string> FindZoneForRecordAsync(string recordName)
        {
            if (string.IsNullOrWhiteSpace(recordName))
            {
                throw new ArgumentException("Record name must not be empty", nameof(recordName));
            }

            var zoneNames = new List<string>();
            var nextUri = "domains?per_page=200";

            while (!string.IsNullOrEmpty(nextUri))
            {
                var response = await _httpClient.GetAsync(nextUri);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "DigitalOcean domain list request failed. Status: {StatusCode}. Response: {Response}. " +
                        "This usually means the API token is invalid, expired, or missing the required scopes.",
                        (int)response.StatusCode, body);

                    throw new InvalidOperationException(
                        $"DigitalOcean API returned {(int)response.StatusCode} ({response.StatusCode}) while listing domains: {body}. " +
                        "Verify the configured API token and its scopes.");
                }

                var page = JsonSerializer.Deserialize<DomainsResponse>(body);
                var domains = page?.Domains ?? Array.Empty<DomainData>();
                zoneNames.AddRange(domains.Where(d => d.Name != null).Select(d => d.Name));

                var next = page?.Links?.Pages?.Next;
                nextUri = string.IsNullOrEmpty(next) ? null : new Uri(next).PathAndQuery.TrimStart('/');
            }

            if (zoneNames.Count == 0)
            {
                throw new InvalidOperationException("DigitalOcean returned an empty or invalid domains list. Aborting.");
            }

            var match = FindBestMatch(zoneNames, recordName.TrimEnd('.'));

            if (match == null)
            {
                throw new InvalidOperationException(
                    $"No DigitalOcean domain found for record '{recordName}'. Ensure the domain exists in this DigitalOcean account.");
            }

            return match;
        }

        /// <summary>
        /// Computes the record name relative to its owning zone, e.g. for zone "example.com" and
        /// record "_acme-challenge.example.com" returns "_acme-challenge"; returns "@" when the
        /// record name is the zone apex itself.
        /// </summary>
        internal static string RelativeRecordName(string zone, string recordName)
        {
            var trimmedRecord = recordName.TrimEnd('.');
            var trimmedZone = zone.TrimEnd('.');

            if (trimmedRecord.Equals(trimmedZone, StringComparison.OrdinalIgnoreCase))
            {
                return "@";
            }

            return trimmedRecord.Substring(0, trimmedRecord.Length - trimmedZone.Length - 1);
        }

        /// <summary>
        /// Finds the zone whose name is the longest suffix match of the target domain,
        /// e.g. for domain "_acme-challenge.www.example.com" and zones {"example.com", "com"},
        /// "example.com" wins because it's the more specific (longer) match.
        /// </summary>
        internal static string FindBestMatch(IEnumerable<string> zones, string domain)
        {
            string best = null;
            foreach (var zone in zones)
            {
                var isMatch = domain.Equals(zone, StringComparison.OrdinalIgnoreCase) ||
                              domain.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);

                if (isMatch && (best == null || zone.Length > best.Length))
                {
                    best = zone;
                }
            }
            return best;
        }
    }
}
