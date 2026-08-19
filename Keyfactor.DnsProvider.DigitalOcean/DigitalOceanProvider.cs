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
                BaseAddress = new Uri("https://api.digitalocean.com/v2/"),
                // DigitalOceanDomainValidator holds a per-key lock across the entire Stage/Cleanup
                // call, including every HTTP request this class makes -- without an explicit bound,
                // a single stalled DigitalOcean connection falls back to HttpClient's 100-second
                // default, and a Create/Delete can make 2-3 sequential requests, so an unrelated
                // legitimate operation for the SAME key could queue behind a hung one for several
                // minutes. 30 seconds is generous for a DNS record CRUD call under normal conditions.
                Timeout = TimeSpan.FromSeconds(30)
            };

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public async Task<bool> CreateRecordAsync(string recordName, string value, string recordType, CancellationToken cancellationToken = default)
        {
            recordName = StripControlCharacters(recordName);
            _logger.LogDebug("Creating {RecordType} record for {RecordName}", recordType, recordName);

            var zone = await FindZoneForRecordAsync(recordName, cancellationToken);
            var relativeName = RelativeRecordName(zone, recordName);

            var payload = new { type = recordType, name = relativeName, data = value, ttl = 300 };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"domains/{zone}/records", content, cancellationToken);
            // Sanitized immediately: this is DigitalOcean-controlled response content, not just
            // plugin-derived values, but it still reaches log/exception sinks below, so it's
            // subject to the same CWE-117 CRLF log-forging risk as any other logged value. Valid
            // JSON never contains raw (unescaped) control characters, so this is a no-op on the
            // success/deserialization path.
            var result = StripControlCharacters(await response.Content.ReadAsStringAsync(cancellationToken));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "DigitalOcean API rejected creation of {RecordType} record '{RelativeName}' in zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    recordType, relativeName, zone, (int)response.StatusCode, result);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)response.StatusCode} ({response.StatusCode}) creating {recordType} record '{relativeName}' in zone '{zone}': {result}");
            }

            var createdId = JsonSerializer.Deserialize<CreateRecordResponse>(result)?.DomainRecord?.Id;
            _logger.LogInformation(
                "Created {RecordType} record '{RelativeName}' ({RecordId}) in DigitalOcean zone '{Zone}'",
                recordType, relativeName, createdId, zone);
            return true;
        }

        public async Task<bool> DeleteRecordAsync(string recordName, string recordType, string expectedValue = null, CancellationToken cancellationToken = default)
        {
            recordName = StripControlCharacters(recordName);
            _logger.LogDebug("Deleting {RecordType} record for {RecordName}", recordType, recordName);

            var zone = await FindZoneForRecordAsync(recordName, cancellationToken);
            var relativeName = RelativeRecordName(zone, recordName);

            var recordsResp = await _httpClient.GetAsync($"domains/{zone}/records?type={recordType}", cancellationToken);
            var recordsBody = StripControlCharacters(await recordsResp.Content.ReadAsStringAsync(cancellationToken));
            if (!recordsResp.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "DigitalOcean API failed to list records for zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    zone, (int)recordsResp.StatusCode, recordsBody);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)recordsResp.StatusCode} ({recordsResp.StatusCode}) listing records in zone '{zone}': {recordsBody}");
            }

            var records = JsonSerializer.Deserialize<RecordsResponse>(recordsBody)?.DomainRecords ?? Array.Empty<RecordData>();

            // When multiple records share the same name/type (e.g. an apex + wildcard SAN both
            // challenging at the same _acme-challenge FQDN with different values), matching by
            // value as well prevents deleting a sibling authorization's still-pending record.
            // expectedValue is only known when the caller staged it itself; without it we fall
            // back to the original name/type-only match to preserve existing single-record behavior.
            var match = expectedValue != null
                ? records.FirstOrDefault(r =>
                    string.Equals(r.Type, recordType, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Name, relativeName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Data, expectedValue, StringComparison.Ordinal))
                : records.FirstOrDefault(r =>
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

            var deleteResp = await _httpClient.DeleteAsync($"domains/{zone}/records/{match.Id}", cancellationToken);

            if (!deleteResp.IsSuccessStatusCode)
            {
                var deleteBody = StripControlCharacters(await deleteResp.Content.ReadAsStringAsync(cancellationToken));
                _logger.LogError(
                    "DigitalOcean API rejected deletion of {RecordType} record '{RelativeName}' ({RecordId}) in zone '{Zone}'. Status: {StatusCode}. Response: {Response}",
                    recordType, relativeName, match.Id, zone, (int)deleteResp.StatusCode, deleteBody);

                throw new InvalidOperationException(
                    $"DigitalOcean API returned {(int)deleteResp.StatusCode} ({deleteResp.StatusCode}) deleting {recordType} record '{relativeName}' in zone '{zone}': {deleteBody}");
            }

            _logger.LogInformation(
                "Deleted {RecordType} record '{RelativeName}' ({RecordId}) in DigitalOcean zone '{Zone}', matched by {MatchMode}",
                recordType, relativeName, match.Id, zone, expectedValue != null ? "value" : "name");
            return true;
        }

        /// <summary>
        /// Fetches all domains (zones) on the account, paging through `links.pages.next`, and
        /// resolves the zone that owns the given record by longest matching name suffix, e.g.
        /// for "_acme-challenge.www.example.com" it tries "www.example.com", then "example.com".
        /// </summary>
        private async Task<string> FindZoneForRecordAsync(string recordName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(recordName))
            {
                throw new ArgumentException("Record name must not be empty", nameof(recordName));
            }

            var zoneNames = new List<string>();
            var nextUri = "domains?per_page=200";

            // Bounds the loop against a malformed/cyclic `next` link (API bug or future change) so
            // a broken pagination response can't hang StageValidation/CleanupValidation forever.
            // 5,000 pages at 200/page is 1,000,000 domains -- far beyond any realistic account size.
            const int maxPages = 5000;
            var pageCount = 0;

            while (!string.IsNullOrEmpty(nextUri))
            {
                pageCount++;
                if (pageCount > maxPages)
                {
                    throw new InvalidOperationException(
                        $"DigitalOcean domain list pagination exceeded {maxPages} pages without terminating; aborting.");
                }

                // `next` (when present) is an ABSOLUTE URL from DigitalOcean's HATEOAS-style
                // pagination, e.g. "https://api.digitalocean.com/v2/domains?page=2&per_page=200".
                // HttpClient.GetAsync uses an absolute URI as-is, ignoring BaseAddress, so passing
                // it straight through resolves correctly; re-deriving a relative path from it
                // (previously done via Uri.PathAndQuery.TrimStart('/')) reintroduces the "/v2"
                // segment and, combined with BaseAddress already ending in "/v2/", doubles it into
                // "/v2/v2/...", which the real API 404s.
                var response = await _httpClient.GetAsync(nextUri, cancellationToken);
                var body = StripControlCharacters(await response.Content.ReadAsStringAsync(cancellationToken));

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

                nextUri = page?.Links?.Pages?.Next;
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
        /// Strips control characters (including CR/LF) from a gateway-supplied record name before
        /// it can reach any log message or exception text. A legitimate hostname never contains
        /// these characters, so this only affects malformed/malicious input — it prevents an
        /// unvalidated record name from forging log lines (CWE-117) via embedded CRLF.
        /// </summary>
        internal static string StripControlCharacters(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return new string(value.Where(c => !char.IsControl(c)).ToArray());
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
