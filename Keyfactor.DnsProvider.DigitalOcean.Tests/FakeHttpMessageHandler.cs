using System.Net;
using System.Net.Http;

namespace Keyfactor.Extensions.DomainValidator.DigitalOcean.Tests
{
    /// <summary>
    /// Routes requests to a caller-supplied responder so DigitalOceanProvider can be
    /// exercised end-to-end without touching the real DigitalOcean API.
    /// </summary>
    internal class FakeHttpMessageHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }

        public static HttpResponseMessage Json(HttpStatusCode status, string body)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
