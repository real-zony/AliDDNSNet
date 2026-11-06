using System.Net;
using System.Net.Sockets;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class PublicIpProviderTests
{
    [Fact]
    public async Task PreservesExplicitLegacyIpv6Source()
    {
        using var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        var configuration = Configuration(shared: "https://legacy.example/ip", type: "AAAA");
        var provider = new PublicIpProvider(http, configuration);

        var address = await provider.GetAsync(AddressFamily.InterNetworkV6, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("2001:db8::11"), address);
        Assert.Equal(new[] { "https://legacy.example/ip" }, handler.Requests);
    }

    [Fact]
    public async Task Ipv6OverrideWinsOverSharedSource()
    {
        using var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration("https://shared.example/ip", "https://v6.example/ip"));

        var ipv4 = await provider.GetAsync(AddressFamily.InterNetwork, CancellationToken.None);
        var ipv6 = await provider.GetAsync(AddressFamily.InterNetworkV6, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.0.2.10"), ipv4);
        Assert.Equal(IPAddress.Parse("2001:db8::22"), ipv6);
        Assert.Equal(new[] { "https://shared.example/ip", "https://v6.example/ip" }, handler.Requests);
    }

    [Fact]
    public async Task AbsentSourcesUseSeparateFamilyDefaults()
    {
        using var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration());

        var ipv4 = await provider.GetAsync(AddressFamily.InterNetwork, CancellationToken.None);
        var ipv6 = await provider.GetAsync(AddressFamily.InterNetworkV6, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("192.0.2.10"), ipv4);
        Assert.Equal(IPAddress.Parse("2001:db8::22"), ipv6);
        Assert.Equal(new[] { "https://api.ipify.org/", "https://api6.ipify.org/" }, handler.Requests);
    }

    [Fact]
    public async Task RejectsWrongFamily()
    {
        using var handler = new SourceHandler();
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration("https://shared.example/ip"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetAsync(AddressFamily.InterNetworkV6, CancellationToken.None));

        Assert.Contains("PublicIpv6Server", error.Message);
        Assert.Equal(new[] { "https://shared.example/ip" }, handler.Requests);
    }

    [Fact]
    public async Task RejectsHttpErrorBody()
    {
        using var handler = new SourceHandler(HttpStatusCode.ServiceUnavailable, "<html>try later</html>");
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration());

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetAsync(AddressFamily.InterNetwork, CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>not an address</html>")]
    [InlineData("192.0.2.10\n192.0.2.11")]
    public async Task RejectsInvalidAddressBody(string body)
    {
        using var handler = new SourceHandler(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => provider.GetAsync(AddressFamily.InterNetwork, CancellationToken.None));
    }

    [Fact]
    public async Task HttpClientTimeoutCoversStalledResponseBody()
    {
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new StalledResponseContent(cleanup.Token);
        using var handler = new DnsCoreHttpHandler((request, fields) =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        var provider = new PublicIpProvider(http, Configuration());

        var pending = provider.GetAsync(AddressFamily.InterNetwork, TestContext.Current.CancellationToken);
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
    public async Task PropagatesInFlightCancellation()
    {
        using var handler = new CancellableHandler();
        using var http = new HttpClient(handler);
        var provider = new PublicIpProvider(http, Configuration());
        using var cancellation = new CancellationTokenSource();

        var pending = provider.GetAsync(AddressFamily.InterNetwork, cancellation.Token);
        await handler.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static ConfigurationModel Configuration(string? shared = null, string? ipv6 = null, string type = "A") => new()
    {
        AccessId = "test-id",
        AccessKey = "test-secret",
        MainDomain = "example.com",
        PublicIpServer = shared,
        PublicIpv6Server = ipv6,
        SubDomains = new[] { new SubDomainRecord(type, "home", 600) }
    };

    private sealed class SourceHandler(HttpStatusCode status = HttpStatusCode.OK, string? body = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            var text = body ?? request.RequestUri.Host switch
            {
                "legacy.example" => " \r\n2001:db8::11\n ",
                "v6.example" or "api6.ipify.org" => " \r\n2001:db8::22\n ",
                _ => " \r\n192.0.2.10\n "
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(text) });
        }
    }

    private sealed class CancellableHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled request must not complete.");
        }
    }
}
