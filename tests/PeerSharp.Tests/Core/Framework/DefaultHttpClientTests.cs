using PeerSharp.Internals.Framework;
using System.Net;
using System.Text;

namespace PeerSharp.Tests.Core.Framework;

public class DefaultHttpClientTests
{
    [Fact]
    public async Task GetByteArrayAsync_DelegatesToInnerClient()
    {
        var handler = new TestHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"))
            };
            return response;
        });

        using var httpClient = new HttpClient(handler);
        var client = new DefaultHttpClient(httpClient);

        var data = await client.GetByteArrayAsync("http://example/", CancellationToken.None);

        Assert.Equal("hello", Encoding.UTF8.GetString(data));
        Assert.Single(handler.Requests);
        Assert.Equal("http://example/", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task SendAsync_DelegatesToInnerClient()
    {
        var handler = new TestHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var httpClient = new HttpClient(handler);
        var client = new DefaultHttpClient(httpClient);

        var request = new HttpRequestMessage(HttpMethod.Get, "http://example/");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public TestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}




