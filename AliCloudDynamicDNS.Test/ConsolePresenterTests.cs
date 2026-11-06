using System.Net;
using System.Net.Sockets;
using System.Text;
using AliCloudDynamicDNS.Cli;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Updating;
using Spectre.Console;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class ConsolePresenterTests
{
    private static ConfigurationModel Configuration() => new()
    {
        AccessId = "fake-access-id-12345678",
        AccessKey = "fake-secret-key-abcdef",
        MainDomain = "example.test",
        PublicIpServer = "https://address.test/",
        SubDomains = new[] { new SubDomainRecord("AAAA", "[blue]www[/]", 600) }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendersExternalTextLiterallyAndMasksCredentials(bool interactive)
    {
        var configuration = Configuration();
        var output = new StringWriter();
        var console = CreateConsole(output, interactive);
        var presenter = new ConsolePresenter(console, interactive, configuration);
        presenter.WriteConfiguration(configuration, uint.MaxValue);
        presenter.WriteError($"[red]remote failure[/] {configuration.AccessId} {configuration.AccessKey}");
        presenter.WriteUpdateResult(new UpdateResult(new[]
        {
            new RecordUpdateResult("[blue]www[/]", "AAAA", "record-1", "2001:db8::1", "2001:db8::10",
                RecordUpdateStatus.Failed, $"[bold]external[/] {configuration.AccessKey}")
        }), true);

        var text = RemoveAnsi(output.ToString());
        Assert.Contains("[red]remote failure[/]", text);
        Assert.Contains("[blue]www[/]", text);
        Assert.Contains("[bold]external[/]", text);
        Assert.Contains("2001:db8::10", text);
        Assert.DoesNotContain(configuration.AccessKey, text);
        Assert.DoesNotContain(configuration.AccessId, text);
    }

    [Fact]
    public void RedirectedOutputHasNoTerminalControls()
    {
        var configuration = Configuration();
        var output = new StringWriter();
        // The caller's interactive hint must not override a redirected output's capabilities.
        var console = CreateConsole(output, false);
        var presenter = new ConsolePresenter(console, true, configuration);
        presenter.WriteConfiguration(configuration, 30);
        presenter.WriteIp(AddressFamily.InterNetworkV6, IPAddress.Parse("2001:db8::10"));
        presenter.WriteError("[red]external[/]\u001b[2J\r\nforged log\u0007");
        presenter.WriteCommandHelp();
        presenter.Clear();
        presenter.WriteUpdateResult(new UpdateResult(new[]
        {
            new RecordUpdateResult("www", "AAAA", "id", "2001:db8::1", "2001:db8::10", RecordUpdateStatus.Updated, null)
        }), true);

        var text = output.ToString();
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain("\nforged log", text);
        Assert.Contains("2001:db8::10", text);
        Assert.Contains("[red]external[/]", text);
    }

    [Fact]
    public void SignedUrlsAndEncodedCredentialsAreNotPrinted()
    {
        var configuration = WithCredentials("access/id+id", "secret/key+value");
        var output = new StringWriter();
        var presenter = new ConsolePresenter(CreateConsole(output, false), false, configuration);
        presenter.WriteError($"Failed https://alidns.aliyuncs.com/?AccessKeyId={Uri.EscapeDataString(configuration.AccessId)}&Signature=abc%2F123&SignatureNonce=n");
        presenter.WriteError($"Credentials {Uri.EscapeDataString(configuration.AccessId)} {Uri.EscapeDataString(configuration.AccessKey)}");

        var text = output.ToString();
        Assert.DoesNotContain("Signature=", text);
        Assert.DoesNotContain("abc%2F123", text);
        Assert.DoesNotContain(configuration.AccessKey, text);
        Assert.DoesNotContain(configuration.AccessId, text);
        Assert.DoesNotContain(Uri.EscapeDataString(configuration.AccessKey), text);
        Assert.DoesNotContain(Uri.EscapeDataString(configuration.AccessId), text);
        Assert.Contains("alidns.aliyuncs.com", text);
    }

    [Fact]
    public void ConfigurationCallEstablishesCredentialRedaction()
    {
        var configuration = Configuration();
        var output = new StringWriter();
        var presenter = new ConsolePresenter(CreateConsole(output, false), false);
        presenter.WriteConfiguration(configuration, 0);
        presenter.WriteError(configuration.AccessKey + " " + configuration.AccessId);

        Assert.DoesNotContain(configuration.AccessKey, output.ToString());
        Assert.DoesNotContain(configuration.AccessId, output.ToString());
    }

    [Fact]
    public void FailedRowsRemainVisibleWhenUnchangedRowsAreHidden()
    {
        var output = new StringWriter();
        var presenter = new ConsolePresenter(CreateConsole(output, false), false);
        presenter.WriteUpdateResult(new UpdateResult(new[]
        {
            new RecordUpdateResult("same-record", "A", "1", "192.0.2.1", "192.0.2.1", RecordUpdateStatus.Unchanged, null),
            new RecordUpdateResult("failed-record", "AAAA", null, null, null, RecordUpdateStatus.Failed, "missing record"),
            new RecordUpdateResult("changed-record", "A", "2", "192.0.2.1", "192.0.2.2", RecordUpdateStatus.Updated, null)
        }, "list failure"), false);

        var text = output.ToString();
        Assert.DoesNotContain("same-record", text);
        Assert.Contains("failed-record", text);
        Assert.Contains("missing record", text);
        Assert.Contains("changed-record", text);
        Assert.Contains("192.0.2.2", text);
        Assert.Contains("list failure", text);
    }

    [Fact]
    public void ConcurrentMessagesRemainWholeLines()
    {
        var output = new StringWriter();
        var presenter = new ConsolePresenter(CreateConsole(output, false), false);
        Parallel.For(0, 100, index => presenter.WriteError($"message-{index}-complete"));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(100, lines.Length);
        for (var index = 0; index < 100; index++)
        {
            Assert.Single(lines, line => line.Contains($"message-{index}-complete", StringComparison.Ordinal));
        }
    }

    private static ConfigurationModel WithCredentials(string accessId, string accessKey) => new()
    {
        AccessId = accessId,
        AccessKey = accessKey,
        MainDomain = "example.test",
        SubDomains = new[] { new SubDomainRecord("A", "www", 600) }
    };

    private static IAnsiConsole CreateConsole(TextWriter output, bool terminal)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new TestOutput(output, terminal),
            Ansi = terminal ? AnsiSupport.Yes : AnsiSupport.No,
            Interactive = terminal ? InteractionSupport.Yes : InteractionSupport.No,
            ColorSystem = terminal ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            EnvironmentVariables = new Dictionary<string, string>()
        });
        console.Profile.Width = 160;
        return console;
    }

    private static string RemoveAnsi(string value) => System.Text.RegularExpressions.Regex.Replace(value, "\u001b\\[[0-?]*[ -/]*[@-~]", "");

    private sealed class TestOutput(TextWriter writer, bool terminal) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;
        public bool IsTerminal => terminal;
        public int Width => 160;
        public int Height => 40;
        public void SetEncoding(Encoding encoding) { }
    }
}
