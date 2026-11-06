using System.Net;
using AliCloudDynamicDNS.AliCloud;
using AliCloudDynamicDNS.AliCloud.ApiRequest;
using AliCloudDynamicDNS.AliCloud.Models;
using AliCloudDynamicDNS.Configuration;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class AliDnsClientTests
{
    [Fact]
    public void SignatureMatchesIndependentFixture()
    {
        var parameters = new Dictionary<string, string>
        {
            ["Action"] = "UpdateDomainRecord",
            ["RecordId"] = "record-v6",
            ["RR"] = "主 机+~",
            ["Type"] = "AAAA",
            ["Value"] = "2001:db8::10",
            ["TTL"] = "600",
            ["Line"] = "telecom"
        };

        var signedQuery = ApiRequestParameters.CreateQuery("test-id", "test-secret", parameters,
            new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), "fixed-nonce");
        var fields = DnsCoreHttpHandler.Query(new Uri($"https://alidns.aliyuncs.com/?{signedQuery}"));

        // Independent Python hmac/hashlib/urllib fixture, not the production signer.
        Assert.Equal("PKwq4g+WEQUaRlUoPNm6ZuYqIOE=", fields["Signature"]);
        Assert.Contains("RR=%E4%B8%BB%20%E6%9C%BA%2B~", signedQuery);
    }

    [Fact]
    public async Task ReadsLaterPages()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => fields["PageNumber"] switch
        {
            "1" => DnsCoreHttpHandler.RecordsPage(2, 1, """
                {"RR":"other","RecordId":"record-1","Value":"192.0.2.1","Type":"A","Line":"default"}
                """),
            "2" => DnsCoreHttpHandler.RecordsPage(2, 2, """
                {"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}
                """),
            _ => throw new InvalidOperationException("Unexpected extra page.")
        });
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        var records = await client.GetRecordsAsync("example.com", CancellationToken.None);

        Assert.Collection(records,
            record => Assert.Equal(new AliCloudRecordModel("other", "record-1", "192.0.2.1", "A", "default"), record),
            record => Assert.Equal(new AliCloudRecordModel("home", "record-v6", "2001:db8::1", "AAAA", "telecom"), record));
        Assert.Equal(new[] { "1", "2" }, handler.Requests.Select(request => request.Fields["PageNumber"]));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("500", request.Fields["PageSize"]);
            Assert.Equal("example.com", request.Fields["DomainName"]);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("alidns.aliyuncs.com", request.Uri.Host);
            Assert.Equal(HttpMethod.Get, request.Method);
        });
    }

    [Fact]
    public async Task PreservesLineWhenUpdating()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => DnsCoreHttpHandler.Json("""
            {"RequestId":"request-update","RecordId":"record-v6"}
            """));
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        await client.UpdateRecordAsync(new AliCloudRecordModel("home", "record-v6", "2001:db8::1", "AAAA", "telecom"),
            IPAddress.Parse("2001:db8::10"), new SubDomainRecord("AAAA", "home", 600), CancellationToken.None);

        var update = Assert.Single(handler.Requests);
        Assert.Equal("UpdateDomainRecord", update.Fields["Action"]);
        Assert.Equal("record-v6", update.Fields["RecordId"]);
        Assert.Equal("home", update.Fields["RR"]);
        Assert.Equal("AAAA", update.Fields["Type"]);
        Assert.Equal("2001:db8::10", update.Fields["Value"]);
        Assert.Equal("600", update.Fields["TTL"]);
        Assert.Equal("telecom", update.Fields["Line"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.OK)]
    public async Task ReportsApiErrorWithoutSignedUrl(HttpStatusCode status)
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => DnsCoreHttpHandler.Json($$"""
            {"Code":"InvalidAccessKeyId.NotFound","Message":"Specified access key test-id / test-secret is not found: {{request.RequestUri}}","RequestId":"request-error"}
            """, status));
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetRecordsAsync("example.com", CancellationToken.None));

        Assert.Contains("InvalidAccessKeyId.NotFound", error.Message);
        Assert.Contains("Specified access key", error.Message);
        Assert.Contains("request-error", error.Message);
        Assert.DoesNotContain("test-id", error.ToString());
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain("Signature=", error.ToString());
        Assert.DoesNotContain("https://", error.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"RecordId\":\"different-record\"}")]
    public async Task RejectsMissingOrMismatchedUpdateRecordId(string json)
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => DnsCoreHttpHandler.Json(json));
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.UpdateRecordAsync(
            new AliCloudRecordModel("home", "record-v4", "192.0.2.1", "A", "default"),
            IPAddress.Parse("192.0.2.10"), new SubDomainRecord("A", "home", 600), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsIncompleteOrRepeatedPages(bool repeated)
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => DnsCoreHttpHandler.RecordsPage(2,
            int.Parse(fields["PageNumber"], System.Globalization.CultureInfo.InvariantCulture),
            fields["PageNumber"] == "1" || repeated
                ? """{"RR":"home","RecordId":"record-v4","Value":"192.0.2.1","Type":"A","Line":"default"}"""
                : ""));
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetRecordsAsync("example.com", CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RejectsHttpErrorWithoutLeakingBody()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>test-secret</html>") });
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRecordsAsync("example.com", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain("Signature=", error.ToString());
    }

    [Fact]
    public async Task HttpClientTimeoutCoversStalledResponseBody()
    {
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new StalledResponseContent(cleanup.Token);
        using var handler = new DnsCoreHttpHandler((request, fields) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var client = new AliDnsClient(http, "test-id", "test-secret");

        var pending = client.GetRecordsAsync("example.com", TestContext.Current.CancellationToken);
        try
        {
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.IsType<TimeoutException>(error.InnerException);
            Assert.False(cleanup.IsCancellationRequested);
        }
        finally
        {
            cleanup.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task PropagatesTransportFailureWithoutSignedUrl()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) =>
            throw new HttpRequestException($"Unable to reach {request.RequestUri}"));
        using var http = new HttpClient(handler);
        var client = new AliDnsClient(http, "test-id", "test-secret");

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetRecordsAsync("example.com", CancellationToken.None));

        Assert.DoesNotContain("test-id", error.ToString());
        Assert.DoesNotContain("Signature=", error.ToString());
    }
}
