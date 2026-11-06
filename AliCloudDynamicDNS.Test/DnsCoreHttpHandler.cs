using System.Net;

namespace AliCloudDynamicDNS.Test;

internal sealed class DnsCoreHttpHandler(
    Func<HttpRequestMessage, IReadOnlyDictionary<string, string>, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = Query(request.RequestUri!);
        Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, fields));
        return Task.FromResult(respond(request, fields));
    }

    internal static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "",
            StringComparer.Ordinal);

    internal static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
    };

    internal static HttpResponseMessage RecordsPage(int totalCount, int page, string records) => Json($$$"""
        {"RequestId":"request-list","TotalCount":{{{totalCount}}},"PageNumber":{{{page}}},"PageSize":500,"DomainRecords":{"Record":[{{{records}}}]}}
        """);

    internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Fields);
}
