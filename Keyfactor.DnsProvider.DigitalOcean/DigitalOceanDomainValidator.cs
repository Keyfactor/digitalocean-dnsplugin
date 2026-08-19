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
        // for it.
        private readonly Dictionary<string, Queue<string>> _stagedValues = new();

        // Serializes ALL Stage/Cleanup calls for the SAME key end-to-end, including the network
        // round-trip -- not just the queue peek/dequeue. A lock held only around the queue
        // read/write (and released across the `await DeleteRecordAsync(...)` call) is not enough:
        // two concurrent CleanupValidation calls for the same key could both peek the same head
        // value before either dequeues it, so one call's delete succeeds while the other's
        // redundant delete finds nothing (idempotent no-op) and ALSO reports success -- silently
        // leaking the second call's real, distinct record and leaving a stale queue entry that
        // nothing ever dequeues. Different keys still run fully in parallel; only same-key
        // operations are serialized. Guarded by `_locksLock`, a separate, short-lived lock used
        // only to get-or-create a key's semaphore -- never held across an await.
        private readonly Dictionary<string, SemaphoreSlim> _keyLocks = new();
        private readonly object _locksLock = new();

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
            var keyLock = GetKeyLock(key);
            await keyLock.WaitAsync(cancellationToken);
            try
            {
                var success = await _provider.CreateRecordAsync(key, value, RecordTypeName, cancellationToken);

                if (success)
                {
                    GetQueue(key, createIfMissing: true).Enqueue(value);
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
            finally
            {
                keyLock.Release();
            }
        }

        public async Task<DomainValidationResult> CleanupValidation(string key, CancellationToken cancellationToken)
        {
            var keyLock = GetKeyLock(key);
            await keyLock.WaitAsync(cancellationToken);
            try
            {
                // Peek (don't remove) the oldest still-pending staged value. It is only actually
                // dequeued below once DeleteRecordAsync confirms success — removing it up front
                // would permanently lose it if the delete failed/was retried, corrupting the queue
                // for any later retry or sibling cleanup call on this key. Holding `keyLock` for
                // the entire method (including the network round-trip) guarantees no other
                // Stage/Cleanup call for this SAME key can observe or mutate the queue in between,
                // so this peek-then-conditionally-dequeue is race-free for a given key.
                var queue = GetQueue(key, createIfMissing: false);
                var outstandingCount = queue?.Count ?? 0;
                var expectedValue = outstandingCount > 0 ? queue.Peek() : null;

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
                    queue.Dequeue();
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
            finally
            {
                keyLock.Release();
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

        private SemaphoreSlim GetKeyLock(string key)
        {
            lock (_locksLock)
            {
                if (!_keyLocks.TryGetValue(key, out var keyLock))
                {
                    keyLock = new SemaphoreSlim(1, 1);
                    _keyLocks[key] = keyLock;
                }
                return keyLock;
            }
        }

        // Only ever called while holding that key's semaphore (see GetKeyLock), so the returned
        // Queue<string> is never accessed by more than one caller at a time and needs no further
        // locking around Enqueue/Peek/Dequeue -- only the lookup/creation in the shared dictionary
        // itself needs the brief `_locksLock` (reused here rather than adding a third lock, since
        // it is already the lock guarding shared-dictionary structural changes for this class).
        private Queue<string> GetQueue(string key, bool createIfMissing)
        {
            lock (_locksLock)
            {
                if (_stagedValues.TryGetValue(key, out var queue))
                {
                    return queue;
                }
                if (!createIfMissing)
                {
                    return null;
                }
                queue = new Queue<string>();
                _stagedValues[key] = queue;
                return queue;
            }
        }
    }
}
