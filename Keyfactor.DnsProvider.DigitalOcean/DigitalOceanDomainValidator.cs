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
                throw new ArgumentException("DigitalOcean_ApiToken is required");
            }

            _provider = new DigitalOceanProvider(apiToken);
        }

        public async Task<DomainValidationResult> StageValidation(string key, string value, CancellationToken cancellationToken)
        {
            try
            {
                var success = await _provider.CreateRecordAsync(key, value, RecordTypeName);

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
                var success = await _provider.DeleteRecordAsync(key, RecordTypeName);

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
