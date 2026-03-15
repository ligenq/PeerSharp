namespace PeerSharp.Internals.Framework;

internal interface IHttpClient
{
    Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken);

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken);
}

internal class DefaultHttpClient : IHttpClient
{
    private readonly HttpClient _client;

    public DefaultHttpClient(HttpClient client) => _client = client;

    public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken)
    {
        return _client.GetByteArrayAsync(url, cancellationToken);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        return _client.SendAsync(request, completionOption, cancellationToken);
    }
}
