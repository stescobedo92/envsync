using System.Net;
using System.Text;

namespace EnvSync.TestSupport;

/// <summary>What the code under test sent: enough to assert the URL and the headers without a network.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>An <see cref="HttpMessageHandler"/> that answers from a delegate and remembers every request.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    public List<RecordedRequest> Requests { get; } = [];

    public static StubHttpMessageHandler Json(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new((_, _) => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, headers));
        return _respond(request, cancellationToken);
    }
}
