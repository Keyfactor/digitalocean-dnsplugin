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
                    ErrorMessage = success ? null : $"Failed to create DNS {RecordTypeName} record for {key}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DigitalOcean StageValidation failed for {RecordType} record '{Key}'", RecordTypeName, key);
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to create {RecordTypeName} record for {key}: {ex.Message}"
                };
            }
        }

        public async Task<DomainValidationResult> CleanupValidation(string key, CancellationToken cancellationToken)
        {
            try
            {
                string expectedValue = null;
                lock (_stagedValuesLock)
                {
                    if (_stagedValues.TryGetValue(key, out var queue) && queue.Count > 0)
                    {
                        expectedValue = queue.Dequeue();
                    }
                }

                var success = await _provider.DeleteRecordAsync(key, RecordTypeName, expectedValue, cancellationToken);

                return new DomainValidationResult
                {
                    Success = success,
                    ErrorMessage = success ? null : $"Failed to delete DNS {RecordTypeName} record for {key}"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DigitalOcean CleanupValidation failed for {RecordType} record '{Key}'", RecordTypeName, key);
                return new DomainValidationResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to delete {RecordTypeName} record for {key}: {ex.Message}"
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
    }
}
