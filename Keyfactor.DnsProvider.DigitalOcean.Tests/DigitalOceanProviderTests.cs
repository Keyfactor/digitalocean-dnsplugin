using System.Net;
using Xunit;

namespace Keyfactor.Extensions.DomainValidator.DigitalOcean.Tests
{
    public class DigitalOceanProviderTests
    {
        [Fact]
        public void FindBestMatch_PicksLongestMatchingSuffix()
        {
            var zones = new[] { "com", "example.com", "other.com" };

            var match = DigitalOceanProvider.FindBestMatch(zones, "_acme-challenge.www.example.com");

            Assert.Equal("example.com", match);
        }

        [Fact]
        public void FindBestMatch_MatchesExactZoneName()
        {
            var zones = new[] { "example.com" };

            var match = DigitalOceanProvider.FindBestMatch(zones, "example.com");

            Assert.Equal("example.com", match);
        }

        [Fact]
        public void FindBestMatch_ReturnsNullWhenNoZoneMatches()
        {
            var zones = new[] { "example.com" };

            var match = DigitalOceanProvider.FindBestMatch(zones, "unrelated-domain.net");

            Assert.Null(match);
        }

        [Fact]
        public void FindBestMatch_DoesNotMatchUnrelatedSuffixSubstring()
        {
            // "notexample.com" must not match zone "example.com" just because it ends with the same characters.
            var zones = new[] { "example.com" };

            var match = DigitalOceanProvider.FindBestMatch(zones, "notexample.com");

            Assert.Null(match);
        }

        [Fact]
        public void RelativeRecordName_StripsZoneSuffix()
        {
            var relative = DigitalOceanProvider.RelativeRecordName("example.com", "_acme-challenge.example.com");

            Assert.Equal("_acme-challenge", relative);
        }

        [Fact]
        public void RelativeRecordName_ReturnsApexMarkerForZoneRoot()
        {
            var relative = DigitalOceanProvider.RelativeRecordName("example.com", "example.com");

            Assert.Equal("@", relative);
        }

        [Fact]
        public async Task CreateRecordAsync_PostsTxtRecordToResolvedZone()
        {
            HttpRequestMessage postRequest = null;

            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                if (req.Method == HttpMethod.Post)
                {
                    postRequest = req;
                    return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                        "{\"domain_record\":{\"id\":123,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"abc123\",\"ttl\":300}}");
                }

                throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
            });

            var provider = new DigitalOceanProvider("token", handler);

            var result = await provider.CreateRecordAsync("_acme-challenge.example.com", "abc123", "TXT");

            Assert.True(result);
            Assert.NotNull(postRequest);
            Assert.EndsWith("domains/example.com/records", postRequest.RequestUri.PathAndQuery);

            var body = await postRequest.Content.ReadAsStringAsync();
            Assert.Contains("\"name\":\"_acme-challenge\"", body);
            Assert.Contains("\"type\":\"TXT\"", body);
            Assert.Contains("\"data\":\"abc123\"", body);
        }

        [Fact]
        public async Task CreateRecordAsync_ThrowsWithApiDetailsWhenApiRejects()
        {
            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                return FakeHttpMessageHandler.Json(HttpStatusCode.BadRequest, "{\"id\":\"invalid_request\",\"message\":\"invalid data\"}");
            });

            var provider = new DigitalOceanProvider("token", handler);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateRecordAsync("_acme-challenge.example.com", "abc123", "TXT"));

            Assert.Contains("400", ex.Message);
            Assert.Contains("example.com", ex.Message);
        }

        [Fact]
        public async Task CreateRecordAsync_ThrowsWhenNoZoneMatches()
        {
            var handler = new FakeHttpMessageHandler(req =>
                FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"domains\":[{\"name\":\"other.com\"}],\"links\":{}}"));

            var provider = new DigitalOceanProvider("token", handler);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateRecordAsync("_acme-challenge.example.com", "abc123", "TXT"));

            Assert.Contains("No DigitalOcean domain found", ex.Message);
        }

        [Fact]
        public async Task CreateRecordAsync_ThrowsAuthErrorNamingLikelyCauseOn401()
        {
            var handler = new FakeHttpMessageHandler(req =>
                FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{\"id\":\"unauthorized\",\"message\":\"Unable to authenticate you.\"}"));

            var provider = new DigitalOceanProvider("bad-token", handler);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => provider.CreateRecordAsync("_acme-challenge.example.com", "abc123", "TXT"));

            Assert.Contains("401", ex.Message);
            Assert.Contains("API token", ex.Message);
        }

        [Fact]
        public async Task CreateRecordAsync_FollowsPaginationToFindZone()
        {
            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.RequestUri.PathAndQuery.Contains("page=2"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"other.com\"}],\"links\":{\"pages\":{\"next\":\"https://api.digitalocean.com/v2/domains?page=2&per_page=200\"}}}");
                }

                if (req.Method == HttpMethod.Post)
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.Created,
                        "{\"domain_record\":{\"id\":123,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"abc123\",\"ttl\":300}}");
                }

                throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
            });

            var provider = new DigitalOceanProvider("token", handler);

            var result = await provider.CreateRecordAsync("_acme-challenge.example.com", "abc123", "TXT");

            Assert.True(result);
        }

        [Fact]
        public async Task DeleteRecordAsync_DeletesMatchingRecord()
        {
            HttpRequestMessage deleteRequest = null;

            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/records"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domain_records\":[{\"id\":7,\"type\":\"TXT\",\"name\":\"_acme-challenge\",\"data\":\"abc123\",\"ttl\":300}]}");
                }

                if (req.Method == HttpMethod.Delete)
                {
                    deleteRequest = req;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
            });

            var provider = new DigitalOceanProvider("token", handler);

            var result = await provider.DeleteRecordAsync("_acme-challenge.example.com", "TXT");

            Assert.True(result);
            Assert.NotNull(deleteRequest);
            Assert.EndsWith("domains/example.com/records/7", deleteRequest.RequestUri.PathAndQuery);
        }

        [Fact]
        public async Task DeleteRecordAsync_IsIdempotentWhenRecordAlreadyGone()
        {
            var handler = new FakeHttpMessageHandler(req =>
            {
                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/domains?"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK,
                        "{\"domains\":[{\"name\":\"example.com\"}],\"links\":{}}");
                }

                if (req.Method == HttpMethod.Get && req.RequestUri.PathAndQuery.Contains("/records"))
                {
                    return FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"domain_records\":[]}");
                }

                throw new InvalidOperationException($"Unexpected request: {req.Method} {req.RequestUri}");
            });

            var provider = new DigitalOceanProvider("token", handler);

            var result = await provider.DeleteRecordAsync("_acme-challenge.example.com", "TXT");

            Assert.True(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Constructor_ThrowsOnMissingApiToken(string apiToken)
        {
            Assert.Throws<ArgumentException>(() => new DigitalOceanProvider(apiToken, new FakeHttpMessageHandler(_ =>
                throw new InvalidOperationException("Should not make HTTP calls"))));
        }
    }
}
