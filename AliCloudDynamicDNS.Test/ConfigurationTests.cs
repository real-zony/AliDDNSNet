using System.Text;
using AliCloudDynamicDNS.Configuration;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class ConfigurationTests
{
    [Fact]
    public async Task ReadsCommentedLegacyConfiguration()
    {
        using var file = new TemporaryConfiguration("""
            {
              // Existing configurations allow comments and trailing commas.
              "accessid": "test-id",
              "accesskey": "test-secret",
              "maindomain": "example.com",
              "publicipserver": "https://legacy.example/ip",
              "ignored": "still accepted",
              "subdomains": [
                { "type": "aaaa", "subdomain": "@", "interval": 600, },
                { "type": "a", "subdomain": "*", "interval": 900 },
                { "type": "A", "subdomain": "a.b", "interval": 1200 },
              ],
            }
            """);

        var configuration = await ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None);

        Assert.Equal("test-id", configuration.AccessId);
        Assert.Equal("test-secret", configuration.AccessKey);
        Assert.Equal("example.com", configuration.MainDomain);
        Assert.Equal("https://legacy.example/ip", configuration.PublicIpServer);
        Assert.Null(configuration.PublicIpv6Server);
        Assert.Collection(configuration.SubDomains,
            target => Assert.Equal(new SubDomainRecord("AAAA", "@", 600), target),
            target => Assert.Equal(new SubDomainRecord("A", "*", 900), target),
            target => Assert.Equal(new SubDomainRecord("A", "a.b", 1200), target));
    }

    [Theory]
    [InlineData("UTF8")]
    [InlineData("UTF16LE")]
    [InlineData("UTF16BE")]
    [InlineData("UTF32LE")]
    [InlineData("UTF32BE")]
    public async Task ReadsBomEncodedLegacyConfiguration(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "UTF8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            "UTF16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
            "UTF16BE" => new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true),
            "UTF32LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
            "UTF32BE" => new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        const string json = """
            {
              // Existing BOM-encoded settings retain the legacy JSON read options.
              "accessid": "bom-test-id",
              "accesskey": "bom-test-secret",
              "maindomain": "example.com",
              "subdomains": [
                { "type": "aaaa", "subdomain": "主机.家庭", "interval": 900, },
              ],
            }
            """;
        using var file = new TemporaryConfiguration(encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray());

        var configuration = await ConfigurationLoader.LoadAsync(file.Path, TestContext.Current.CancellationToken);

        Assert.Equal("bom-test-id", configuration.AccessId);
        Assert.Equal("bom-test-secret", configuration.AccessKey);
        Assert.Equal("example.com", configuration.MainDomain);
        Assert.Equal(new SubDomainRecord("AAAA", "主机.家庭", 900), Assert.Single(configuration.SubDomains));
        Assert.Null(configuration.PublicIpServer);
        Assert.Null(configuration.PublicIpv6Server);
    }

    [Theory]
    [InlineData("UTF8")]
    [InlineData("UTF8BOM")]
    [InlineData("UTF16LE")]
    [InlineData("UTF32LE")]
    public async Task ReadsNonSeekableLegacyConfigurationPath(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "UTF8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            "UTF8BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            "UTF16LE" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
            "UTF32LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingName))
        };
        const string json = """
            {"accessid":"pipe-id","accesskey":"pipe-secret","maindomain":"example.com","subdomains":[{"type":"aaaa","subdomain":"主机.家庭","interval":900}]}
            """;
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(json)).ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(10));
        await using var file = await NonSeekableConfigurationFile.CreateAsync(bytes, cancellation.Token);

        var pending = ConfigurationLoader.LoadAsync(file.Path, cancellation.Token);
        await file.CompleteWritingAsync();
        var configuration = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("pipe-id", configuration.AccessId);
        Assert.Equal("pipe-secret", configuration.AccessKey);
        Assert.Equal("example.com", configuration.MainDomain);
        Assert.Equal(new SubDomainRecord("AAAA", "主机.家庭", 900), Assert.Single(configuration.SubDomains));
        Assert.Null(configuration.PublicIpServer);
        Assert.Null(configuration.PublicIpv6Server);
    }

    [Fact]
    public async Task DoesNotFillAbsentSourcesWithIpv4Default()
    {
        using var file = new TemporaryConfiguration(ValidJson());

        var configuration = await ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None);

        Assert.Null(configuration.PublicIpServer);
        Assert.Null(configuration.PublicIpv6Server);
    }

    [Theory]
    [InlineData("MX")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RejectsUnsupportedTypeBeforeUse(string type)
    {
        using var file = new TemporaryConfiguration(ValidJson(type: type));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.Contains("SubDomains[0].Type", error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Theory]
    [InlineData("AccessId")]
    [InlineData("AccessKey")]
    [InlineData("MainDomain")]
    public async Task RejectsMissingRequiredFieldWithoutLeakingCredentials(string field)
    {
        var json = ValidJson().Replace($"\"{field}\": \"{(field == "AccessId" ? "test-id" : field == "AccessKey" ? "test-secret" : "example.com")}\",", "");
        using var file = new TemporaryConfiguration(json);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.Contains(field, error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Theory]
    [InlineData("[]", "SubDomains")]
    [InlineData("null", "SubDomains")]
    [InlineData("[null]", "SubDomains[0]")]
    [InlineData("[{\"Type\":\"A\",\"SubDomain\":\"\",\"Interval\":600}]", "SubDomains[0].SubDomain")]
    public async Task RejectsEmptyOrInvalidTargets(string targets, string field)
    {
        using var file = new TemporaryConfiguration($$"""
            {"AccessId":"test-id","AccessKey":"test-secret","MainDomain":"example.com","SubDomains":{{targets}}}
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("PublicIpServer", "ftp://source.example/ip")]
    [InlineData("PublicIpv6Server", "/relative")]
    [InlineData("PublicIpServer", "")]
    public async Task RejectsInvalidSourceUri(string field, string source)
    {
        using var file = new TemporaryConfiguration(ValidJson(extra: $"\"{field}\":\"{source}\","));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.Contains(field, error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Fact]
    public async Task MalformedJsonDoesNotExposeContent()
    {
        using var file = new TemporaryConfiguration("{\"AccessKey\":\"test-secret\",\"SubDomains\":[BROKEN]}");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Fact]
    public async Task InvalidFieldValueReportsTargetIndexWithoutJsonContent()
    {
        using var file = new TemporaryConfiguration(ValidJson().Replace("\"Interval\": 600", "\"Interval\": \"test-secret\""));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ConfigurationLoader.LoadAsync(file.Path, CancellationToken.None));

        Assert.Contains("SubDomains[0].Interval", error.Message);
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using var file = new TemporaryConfiguration(ValidJson());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ConfigurationLoader.LoadAsync(file.Path, cancellation.Token));
    }

    private static string ValidJson(string type = "A", string extra = "") => $$"""
        {
          "AccessId": "test-id",
          "AccessKey": "test-secret",
          "MainDomain": "example.com",
          {{extra}}
          "SubDomains": [{ "Type": "{{type}}", "SubDomain": "home", "Interval": 600 }]
        }
        """;

    private sealed class TemporaryConfiguration : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"alidns-config-{Guid.NewGuid():N}.json");

        public TemporaryConfiguration(string json) => File.WriteAllText(Path, json);

        public TemporaryConfiguration(byte[] bytes) => File.WriteAllBytes(Path, bytes);

        public void Dispose() => File.Delete(Path);
    }
}
