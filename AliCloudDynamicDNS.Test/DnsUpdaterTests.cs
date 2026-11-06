using System.Net;
using AliCloudDynamicDNS.AliCloud;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;
using AliCloudDynamicDNS.Updating;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class DnsUpdaterTests
{
    [Fact]
    public async Task MatchesSameNameByType()
    {
        using var handler = StandardHandler("""
            {"RR":"HOME","RecordId":"record-v4","Value":"192.0.2.1","Type":"a","Line":"default"},
            {"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}
            """, 2);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new("A", "home", 600), new("AAAA", "home", 900)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Collection(result.Records,
            row => Assert.Equal(("record-v4", "192.0.2.10", RecordUpdateStatus.Updated), (row.RecordId, row.NewValue, row.Status)),
            row => Assert.Equal(("record-v6", "2001:db8::10", RecordUpdateStatus.Updated), (row.RecordId, row.NewValue, row.Status)));
        var writes = Writes(handler);
        Assert.Equal(2, writes.Length);
        Assert.Equal("record-v4", writes[0].Fields["RecordId"]);
        Assert.Equal("A", writes[0].Fields["Type"]);
        Assert.Equal("192.0.2.10", writes[0].Fields["Value"]);
        Assert.Equal("record-v6", writes[1].Fields["RecordId"]);
        Assert.Equal("AAAA", writes[1].Fields["Type"]);
        Assert.Equal("2001:db8::10", writes[1].Fields["Value"]);
        Assert.Equal("telecom", writes[1].Fields["Line"]);
        Assert.Equal("900", writes[1].Fields["TTL"]);
    }

    [Theory]
    [InlineData("A", "v4.example", "192.0.2.10")]
    [InlineData("AAAA", "v6.example", "2001:db8::10")]
    public async Task FetchesOnlyConfiguredFamilyOncePerCycle(string type, string host, string expectedAddress)
    {
        var oldValue = type == "A" ? "192.0.2.1" : "2001:db8::1";
        using var handler = StandardHandler($$"""
            {"RR":"home","RecordId":"record-home","Value":"{{oldValue}}","Type":"{{type}}","Line":"default"},
            {"RR":"backup","RecordId":"record-backup","Value":"{{oldValue}}","Type":"{{type}}","Line":"default"}
            """, 2);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new(type, "home", 600), new(type, "backup", 600)));

        var first = await updater.UpdateAsync(CancellationToken.None);
        var second = await updater.UpdateAsync(CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.All(first.Records.Concat(second.Records), row => Assert.Equal(expectedAddress, row.NewValue));
        var sources = handler.Requests.Where(request => request.Uri.Host != "alidns.aliyuncs.com").ToArray();
        Assert.Equal(2, sources.Length);
        Assert.All(sources, request => Assert.Equal(host, request.Uri.Host));
        Assert.Equal(4, Writes(handler).Length);
        Assert.All(Writes(handler), request => Assert.Equal(expectedAddress, request.Fields["Value"]));
    }

    [Fact]
    public async Task MissingAndAmbiguousTargetsFailWithoutWritingButOtherTargetsContinue()
    {
        using var handler = StandardHandler("""
            {"RR":"dup","RecordId":"dup-1","Value":"192.0.2.1","Type":"A","Line":"default"},
            {"RR":"DUP","RecordId":"dup-2","Value":"192.0.2.2","Type":"A","Line":"telecom"},
            {"RR":"okay","RecordId":"record-okay","Value":"192.0.2.3","Type":"A","Line":"default"}
            """, 3);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new("A", "missing", 600), new("A", "dup", 600), new("A", "okay", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(new[] { RecordUpdateStatus.Failed, RecordUpdateStatus.Failed, RecordUpdateStatus.Updated }, result.Records.Select(row => row.Status));
        Assert.Contains("missing", result.Records[0].Error!);
        Assert.Contains("A", result.Records[0].Error!);
        Assert.Contains("ambiguous", result.Records[1].Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Records[0].RecordId);
        Assert.Null(result.Records[1].RecordId);
        Assert.Equal("record-okay", Assert.Single(Writes(handler)).Fields["RecordId"]);
    }

    [Fact]
    public async Task EquivalentIpv6AddressIsUnchanged()
    {
        using var handler = StandardHandler("""
            {"RR":"home","RecordId":"record-v6","Value":"2001:0db8:0000:0000:0000:0000:0000:0010","Type":"AAAA","Line":"telecom"}
            """, 1);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new SubDomainRecord("AAAA", "home", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(RecordUpdateStatus.Unchanged, Assert.Single(result.Records).Status);
        Assert.Empty(Writes(handler));
    }

    [Fact]
    public async Task Ipv6FetchFailureDoesNotSuppressIpv4Update()
    {
        using var handler = StandardHandler("""
            {"RR":"home","RecordId":"record-v4","Value":"192.0.2.1","Type":"A","Line":"default"},
            {"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}
            """, 2, failIpv6: true);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new("AAAA", "home", 600), new("A", "home", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecordUpdateStatus.Failed, result.Records[0].Status);
        Assert.NotNull(result.Records[0].Error);
        Assert.Equal(RecordUpdateStatus.Updated, result.Records[1].Status);
        Assert.Equal("record-v4", Assert.Single(Writes(handler)).Fields["RecordId"]);
    }

    [Fact]
    public async Task WrongFamilyNeverProducesAaaaWriteOrFallback()
    {
        using var handler = StandardHandler("""
            {"RR":"home","RecordId":"record-v4","Value":"192.0.2.1","Type":"A","Line":"default"},
            {"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}
            """, 2);
        using var http = new HttpClient(handler);
        var configuration = new ConfigurationModel
        {
            AccessId = "test-id",
            AccessKey = "test-secret",
            MainDomain = "example.com",
            PublicIpServer = "https://v4.example/ip",
            SubDomains = new[] { new SubDomainRecord("A", "home", 600), new SubDomainRecord("AAAA", "home", 600) }
        };
        var updater = CreateUpdater(http, configuration);

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecordUpdateStatus.Updated, result.Records[0].Status);
        Assert.Equal(RecordUpdateStatus.Failed, result.Records[1].Status);
        Assert.Contains("PublicIpv6Server", result.Records[1].Error!);
        Assert.Equal("record-v4", Assert.Single(Writes(handler)).Fields["RecordId"]);
        var sources = handler.Requests.Where(request => request.Uri.Host != "alidns.aliyuncs.com").ToArray();
        Assert.Equal(2, sources.Length);
        Assert.All(sources, request => Assert.Equal("v4.example", request.Uri.Host));
    }

    [Fact]
    public async Task SecondUpdateFailurePreservesResultsAndContinuesToThird()
    {
        using var handler = StandardHandler("""
            {"RR":"first","RecordId":"record-first","Value":"192.0.2.1","Type":"A","Line":"default"},
            {"RR":"second","RecordId":"record-second","Value":"192.0.2.1","Type":"A","Line":"default"},
            {"RR":"third","RecordId":"record-third","Value":"192.0.2.1","Type":"A","Line":"default"}
            """, 3, failRecordId: "record-second");
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new("A", "first", 600), new("A", "second", 600), new("A", "third", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { RecordUpdateStatus.Updated, RecordUpdateStatus.Failed, RecordUpdateStatus.Updated }, result.Records.Select(row => row.Status));
        Assert.Contains("RecordForbidden", result.Records[1].Error!);
        Assert.Equal(new[] { "record-first", "record-second", "record-third" }, Writes(handler).Select(request => request.Fields["RecordId"]));
    }

    [Fact]
    public async Task ListFailureRemainsGlobalFailureWithoutFetchingOrWriting()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) => DnsCoreHttpHandler.Json("""
            {"Code":"DomainNotFound","Message":"Domain not found","RequestId":"list-failed"}
            """, HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new SubDomainRecord("A", "home", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("DomainNotFound", result.Error!);
        Assert.Empty(result.Records);
        Assert.Equal("DescribeDomainRecords", Assert.Single(handler.Requests).Fields["Action"]);
    }

    [Fact]
    public async Task FindsAndUpdatesTargetOnLaterPage()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) =>
        {
            if (request.RequestUri!.Host == "v6.example")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("2001:db8::10") };
            if (fields["Action"] == "UpdateDomainRecord")
                return DnsCoreHttpHandler.Json("""{"RecordId":"record-v6","RequestId":"updated"}""");
            return fields["PageNumber"] == "1"
                ? DnsCoreHttpHandler.RecordsPage(2, 1, """{"RR":"other","RecordId":"record-other","Value":"192.0.2.1","Type":"A","Line":"default"}""")
                : DnsCoreHttpHandler.RecordsPage(2, 2, """{"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}""");
        });
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new SubDomainRecord("AAAA", "home", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(RecordUpdateStatus.Updated, Assert.Single(result.Records).Status);
        Assert.Equal("record-v6", Assert.Single(Writes(handler)).Fields["RecordId"]);
    }

    [Fact]
    public async Task RejectsUnsupportedTargetBeforeAnyHttpRequest()
    {
        using var handler = new DnsCoreHttpHandler((request, fields) =>
            throw new InvalidOperationException("Invalid configuration must not reach HTTP."));
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new("A", "home", 600), new("MX", "mail", 600)));

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Type", result.Error!);
        Assert.Empty(result.Records);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Ipv6HttpTimeoutDoesNotSuppressIpv4Update()
    {
        using var handler = new TimeoutHandler("ipv6");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var updater = CreateUpdater(http, Configuration(new("AAAA", "home", 600), new("A", "home", 600)));

        var result = await updater.UpdateAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(RecordUpdateStatus.Failed, result.Records[0].Status);
        Assert.NotNull(result.Records[0].Error);
        Assert.Equal(RecordUpdateStatus.Updated, result.Records[1].Status);
        Assert.Equal("192.0.2.10", result.Records[1].NewValue);
        var update = Assert.Single(handler.Requests, request =>
            request.Fields.TryGetValue("Action", out var action) && action == "UpdateDomainRecord");
        Assert.Equal("record-v4", update.Fields["RecordId"]);
        Assert.Equal("A", update.Fields["Type"]);
        Assert.Equal("192.0.2.10", update.Fields["Value"]);
    }

    [Fact]
    public async Task MiddleUpdateHttpTimeoutPreservesFirstResultAndContinuesToThird()
    {
        using var handler = new TimeoutHandler("middle-update");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var updater = CreateUpdater(http, Configuration(new("A", "first", 600), new("A", "second", 600), new("A", "third", 600)));

        var result = await updater.UpdateAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(new[] { RecordUpdateStatus.Updated, RecordUpdateStatus.Failed, RecordUpdateStatus.Updated },
            result.Records.Select(row => row.Status));
        Assert.NotNull(result.Records[1].Error);
        Assert.Equal("192.0.2.10", result.Records[0].NewValue);
        Assert.Equal("192.0.2.10", result.Records[2].NewValue);
        Assert.Equal(new[] { "record-first", "record-second", "record-third" }, handler.Requests
            .Where(request => request.Fields.TryGetValue("Action", out var action) && action == "UpdateDomainRecord")
            .Select(request => request.Fields["RecordId"]));
    }

    [Fact]
    public async Task ListHttpTimeoutReturnsGlobalFailure()
    {
        using var handler = new TimeoutHandler("list");
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var updater = CreateUpdater(http, Configuration(new SubDomainRecord("A", "home", 600)));

        var result = await updater.UpdateAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Records);
        Assert.Equal("DescribeDomainRecords", Assert.Single(handler.Requests).Fields["Action"]);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("ip")]
    [InlineData("update")]
    public async Task PropagatesInFlightCancellationAtEveryNetworkStage(string stage)
    {
        using var handler = new CancellationHandler(stage);
        using var http = new HttpClient(handler);
        var updater = CreateUpdater(http, Configuration(new SubDomainRecord("A", "home", 600)));
        using var cancellation = new CancellationTokenSource();

        var pending = updater.UpdateAsync(cancellation.Token);
        await handler.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static ConfigurationModel Configuration(params SubDomainRecord[] targets) => new()
    {
        AccessId = "test-id",
        AccessKey = "test-secret",
        MainDomain = "example.com",
        PublicIpServer = "https://v4.example/ip",
        PublicIpv6Server = "https://v6.example/ip",
        SubDomains = targets
    };

    private static DnsUpdater CreateUpdater(HttpClient http, ConfigurationModel configuration) =>
        new(configuration, new AliDnsClient(http, configuration.AccessId, configuration.AccessKey), new PublicIpProvider(http, configuration));

    private static DnsCoreHttpHandler.CapturedRequest[] Writes(DnsCoreHttpHandler handler) => handler.Requests
        .Where(request => request.Fields.TryGetValue("Action", out var action) && action == "UpdateDomainRecord").ToArray();

    private static DnsCoreHttpHandler StandardHandler(string records, int count, bool failIpv6 = false, string? failRecordId = null) =>
        new((request, fields) =>
        {
            if (request.RequestUri!.Host == "v4.example")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("192.0.2.10") };
            if (request.RequestUri.Host == "v6.example")
                return new HttpResponseMessage(failIpv6 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(failIpv6 ? "<html>unavailable</html>" : "2001:db8::10") };
            if (fields["Action"] == "DescribeDomainRecords")
                return DnsCoreHttpHandler.RecordsPage(count, 1, records);
            if (fields["RecordId"] == failRecordId)
                return DnsCoreHttpHandler.Json("""{"Code":"RecordForbidden","Message":"Update forbidden","RequestId":"update-failed"}""", HttpStatusCode.BadRequest);
            return DnsCoreHttpHandler.Json($$"""{"RecordId":"{{fields["RecordId"]}}","RequestId":"updated"}""");
        });

    private sealed class TimeoutHandler(string timedOutStage) : HttpMessageHandler
    {
        internal List<DnsCoreHttpHandler.CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var fields = DnsCoreHttpHandler.Query(uri);
            Requests.Add(new DnsCoreHttpHandler.CapturedRequest(request.Method, uri, fields));
            var stage = uri.Host == "v6.example" ? "ipv6"
                : uri.Host == "v4.example" ? "ipv4"
                : fields["Action"] == "DescribeDomainRecords" ? "list"
                : fields["RecordId"] == "record-second" ? "middle-update" : "update";
            if (stage == timedOutStage)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The timed-out HTTP request must not complete.");
            }

            if (stage == "ipv4")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("192.0.2.10") };
            if (stage == "list")
                return timedOutStage == "ipv6"
                    ? DnsCoreHttpHandler.RecordsPage(2, 1, """
                        {"RR":"home","RecordId":"record-v4","Value":"192.0.2.1","Type":"A","Line":"default"},
                        {"RR":"home","RecordId":"record-v6","Value":"2001:db8::1","Type":"AAAA","Line":"telecom"}
                        """)
                    : DnsCoreHttpHandler.RecordsPage(3, 1, """
                        {"RR":"first","RecordId":"record-first","Value":"192.0.2.1","Type":"A","Line":"default"},
                        {"RR":"second","RecordId":"record-second","Value":"192.0.2.1","Type":"A","Line":"default"},
                        {"RR":"third","RecordId":"record-third","Value":"192.0.2.1","Type":"A","Line":"default"}
                        """);
            return DnsCoreHttpHandler.Json($$$"""{"RecordId":"{{{fields["RecordId"]}}}","RequestId":"updated"}""");
        }
    }

    private sealed class CancellationHandler(string blockedStage) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = DnsCoreHttpHandler.Query(request.RequestUri!);
            var stage = request.RequestUri!.Host == "v4.example" ? "ip"
                : fields["Action"] == "DescribeDomainRecords" ? "list" : "update";
            if (stage == blockedStage)
            {
                Started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled request must not complete.");
            }

            return stage == "list"
                ? DnsCoreHttpHandler.RecordsPage(1, 1, """{"RR":"home","RecordId":"record-v4","Value":"192.0.2.1","Type":"A","Line":"default"}""")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("192.0.2.10") };
        }
    }
}
