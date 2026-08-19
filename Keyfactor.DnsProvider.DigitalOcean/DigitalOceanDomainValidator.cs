// Copyright 2026 Keyfactor
// Licensed under the Apache License, Version 2.0
using Keyfactor.AnyGateway.Extensions;
using Keyfactor.Logging;
using Microsoft.Extensions.Logging;

namespace Keyfactor.Extensions.DomainValidator.DigitalOcean
{
    /// <summary>
    /// DigitalOcean domain validator for ACME DNS-01 challenges. Publishes TXT records
    /// in DigitalOcean-hosted domains. Authenticates via a Bearer Personal Access Token.
    /// </summary>
    public class DigitalOceanDomainValidator : IDomainValidator
    {
        private static readonly ILogger _logger = LogHandler.GetClassLogger<DigitalOceanDomainValidator>();

        private const string ValidationTypeName = "dns-01";
        private const string RecordTypeName = "TXT";

        private DigitalOceanProvider _provider;
        private Dictionary<string, object> _configuration;

        // Tracks the value staged for each key so CleanupValidation can disambiguate between
        // multiple TXT records sharing the same name (e.g. an apex + wildcard SAN both challenging
        // at the same _acme-challenge FQDN). CleanupValidation's own signature (key only, no value)
        // can't tell us which record to delete, so this queue records staging order per key on a
        // best-effort FIFO basis: cleanup for a key removes the oldest still-pending value staged
        // for it. A lock guards concurrent Stage/Cleanup calls across SANs on the same instance.
        private readonly Dictionary<string, Queue<string>> _stagedValues = new();
        private readonly object _stagedValuesLock = new();

        public DigitalOceanDomainValidator()
        {
        }

        // Internal constructor to allow unit tests to inject a fake provider without going through
        // Initialize (which requires a real IDomainValidatorConfigProvider and constructs its own
        // DigitalOceanProvider from a config-supplied API token).
        internal DigitalOceanDomainValidator(DigitalOceanProvider provider)
        {
            _provider = provider;
        }

        public Dictionary<string, PropertyConfigInfo> GetDomainValidatorAnnotations()
        {
            return new Dictionary<string, PropertyConfigInfo>()
            {
                ["DigitalOcean_ApiToken"] = new PropertyConfigInfo()
                {
                    Comments = "DigitalOcean Personal Access Token with domain read/create/delete scopes (Required)",
                    Hidden = true,
                    DefaultValue = "",
                    Type = "Secret"
                }
            };
        }

        public string GetValidationType() => ValidationTypeName;

        public void Initialize(IDomainValidatorConfigProvider configProvider)
        {
            _configuration = configProvider.DomainValidationConfiguration;

            var apiToken = GetConfigValue("DigitalOcean_ApiToken");

            if (string.IsNullOrWhiteSpace(apiToken))
            {
                _logger.LogWarning("DigitalOcean_ApiToken is missing or empty; plugin initialization cannot proceed");
                throw new ArgumentException("DigitalOcean_ApiToken is required");
            }

            _provider = new DigitalOceanProvider(apiToken);
        }

        public async Task<DomainValidationResult> StageValidation(string key, string value, CancellationToken cancellationToken)
        {
            try
            {
                var success = await _provider.CreateRecordAsync(key, value, RecordTypeName, cancellationToken);

                if (success)
                {
                    lock (_stagedValuesLock)
                    {
                        if (!_stagedValues.TryGetValue(key, out var queue))
                        {
                            queue = new Queue<string>();
                            _stagedValues[key] = queue;
                        }
                        queue.Enqueue(value);
                    }
                }

                return new DomainValidationResult
                {
                    Success = success,
                    ErrorMessage = success ? null : $"Failed to create DNS {RecordTypeName} record for {SafeForLog(key)}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DigitalOcean StageValidation failed for {RecordType} record '{Key}'", RecordTypeName, SafeForLog(key));
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to create {RecordTypeName} record for {SafeForLog(key)}: {ex.Message}"
                };
            }
        }

        public async Task<DomainValidationResult> CleanupValidation(string key, CancellationToken cancellationToken)
        {
            try
            {
                // Peek (don't remove) the oldest still-pending staged value. It is only actually
                // dequeued below once DeleteRecordAsync confirms success — removing it up front
                // would permanently lose it if the delete failed/was retried, corrupting the queue
                // for any later retry or sibling cleanup call on this key.
                string expectedValue = null;
                int outstandingCount;
                lock (_stagedValuesLock)
                {
                    if (_stagedValues.TryGetValue(key, out var queue) && queue.Count > 0)
                    {
                        expectedValue = queue.Peek();
                        outstandingCount = queue.Count;
                    }
                    else
                    {
                        outstandingCount = 0;
                    }
                }

                if (outstandingCount > 1)
                {
                    // CleanupValidation's own contract gives us no challenge value to match against
                    // (only `key`), so when more than one value is outstanding for the same key
                    // (e.g. an apex + wildcard SAN sharing one _acme-challenge FQDN) we cannot know
                    // FOR CERTAIN which one this specific cleanup call is for. We fall back to a
                    // best-effort FIFO match (oldest staged, oldest cleaned up) rather than refusing
                    // to clean up at all, but that assumption can be wrong if completion order
                    // doesn't match staging order -- surfacing it here so it's operationally visible
                    // rather than a silent, unverifiable guess.
                    _logger.LogWarning(
                        "{Count} {RecordType} values are still staged for '{Key}'; cleanup will match the oldest staged value on a best-effort basis, since CleanupValidation does not receive the specific challenge value",
                        outstandingCount, RecordTypeName, SafeForLog(key));
                }

                var success = await _provider.DeleteRecordAsync(key, RecordTypeName, expectedValue, cancellationToken);

                if (success && expectedValue != null)
                {
                    lock (_stagedValuesLock)
                    {
                        if (_stagedValues.TryGetValue(key, out var queue) && queue.Count > 0 && queue.Peek() == expectedValue)
                        {
                            queue.Dequeue();
                        }
                    }
                }

                return new DomainValidationResult
                {
                    Success = success,
                    ErrorMessage = success ? null : $"Failed to delete DNS {RecordTypeName} record for {SafeForLog(key)}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DigitalOcean CleanupValidation failed for {RecordType} record '{Key}'", RecordTypeName, SafeForLog(key));
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to delete {RecordTypeName} record for {SafeForLog(key)}: {ex.Message}"
                };
            }
        }

        public async Task ValidateConfiguration(Dictionary<string, object> configuration)
        {
            _configuration = configuration;

            var apiToken = GetConfigValue("DigitalOcean_ApiToken");
            if (string.IsNullOrWhiteSpace(apiToken))
            {
                _logger.LogWarning("DigitalOcean_ApiToken is missing or empty; configuration validation failed");
                throw new ArgumentException("DigitalOcean_ApiToken is required");
            }

            await Task.CompletedTask;
        }

        private string GetConfigValue(string key)
        {
            if (_configuration != null && _configuration.TryGetValue(key, out var value))
            {
                return value?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }

        // DigitalOceanProvider sanitizes recordName before using it in ITS OWN log/exception
        // messages, but that sanitized copy never crosses back into this class's `key` parameter
        // (strings are immutable/passed by value) -- this class has its own independent log and
        // ErrorMessage call sites that log/embed the raw `key`, so it needs its own sanitization
        // pass to close the same CWE-117 CRLF log-forging gap at this layer.
        private static string SafeForLog(string key) => DigitalOceanProvider.StripControlCharacters(key);
    }
}
