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
    }
}
