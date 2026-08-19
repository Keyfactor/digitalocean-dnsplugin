using System.Net;
using Xunit;

namespace Keyfactor.Extensions.DomainValidator.DigitalOcean.Tests
{
    public class DigitalOceanDomainValidatorTests
    {
        [Fact]
        public void GetValidationType_ReturnsDns01()
        {
            var validator = new DigitalOceanDomainValidator();

            Assert.Equal("dns-01", validator.GetValidationType());
        }

        [Fact]
        public void GetDomainValidatorAnnotations_DeclaresApiToken()
        {
            var validator = new DigitalOceanDomainValidator();

            var annotations = validator.GetDomainValidatorAnnotations();

            Assert.True(annotations.ContainsKey("DigitalOcean_ApiToken"));
            Assert.Equal("Secret", annotations["DigitalOcean_ApiToken"].Type);
            Assert.True(annotations["DigitalOcean_ApiToken"].Hidden);
        }

        [Fact]
        public async Task ValidateConfiguration_ThrowsWhenApiTokenMissing()
        {
            var validator = new DigitalOceanDomainValidator();
            var config = new Dictionary<string, object>();

            await Assert.ThrowsAsync<ArgumentException>(() => validator.ValidateConfiguration(config));
        }

        [Fact]
        public async Task ValidateConfiguration_SucceedsWhenApiTokenPresent()
        {
            var validator = new DigitalOceanDomainValidator();
            var config = new Dictionary<string, object> { ["DigitalOcean_ApiToken"] = "token" };

            await validator.ValidateConfiguration(config);
        }

        [Fact]
        public async Task CleanupValidation_DeletesTheStagedValue_NotJustTheFirstSameNameRecord()
        {
            // Simulates an apex + wildcard SAN pair: both authorizations challenge at the identical
            // _acme-challenge FQDN with different values, staged in order [value-A, value-B]. The
            // DigitalOcean records list is returned in the OPPOSITE order on purpose — a name-only
            // match (the pre-fix behavior) would delete value-B's record on the first cleanup call
            // even though value-A's authorization is the one being cleaned up.
            var deletedIds = new List<string>();

            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                if (req.Method == HttpMethod.Post)
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                        "{\"domain_record\":{\"id\":1,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"ignored\",\"ttl\":300}}");
                }

                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/records"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domain_records\":[" +
                        "{\"id\":11,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"value-B\",\"ttl\":300}," +
                        "{\"id\":10,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"value-A\",\"ttl\":300}]}");
                }

                if (req.Method == HttpMethod.Delete)
                {
                    deletedIds.Add(req.RequestUri.PathAndQuery.Split('/').Last());
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
            });

            var provider = new DigitalOceanProvider("token", handler);
            var validator = new DigitalOceanDomainValidator(provider);

            const string key = "_acme-challenge.example.com";
            var stageA = await validator.StageValidation(key, "value-A", CancellationToken.None);
            var stageB = await validator.StageValidation(key, "value-B", CancellationToken.None);
            Assert.True(stageA.Success);
            Assert.True(stageB.Success);

            var cleanupA = await validator.CleanupValidation(key, CancellationToken.None);
            var cleanupB = await validator.CleanupValidation(key, CancellationToken.None);

            Assert.True(cleanupA.Success);
            Assert.True(cleanupB.Success);
            Assert.Equal(new[] { "10", "11" }, deletedIds);
        }
    }
}
