using System.CommandLine;
using AliCloudDynamicDNS.AliCloud;
using AliCloudDynamicDNS.Cli;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;
using AliCloudDynamicDNS.Runtime;
using AliCloudDynamicDNS.Updating;
using Spectre.Console;

namespace AliCloudDynamicDNS;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        CliApplication.CreateRootCommand(ExecuteAsync).Parse(args).InvokeAsync();

    private static async Task<int> ExecuteAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var terminal = !System.Console.IsOutputRedirected;
        var interactive = terminal && !System.Console.IsInputRedirected;
        var noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(System.Console.Out),
            Ansi = terminal && !noColor ? AnsiSupport.Detect : AnsiSupport.No,
            Interactive = interactive ? InteractionSupport.Detect : InteractionSupport.No,
            ColorSystem = noColor ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect
        });
        interactive &= console.Profile.Capabilities.Interactive;
        var presenter = new ConsolePresenter(console, interactive);
        try
        {
            var path = options.FilePath ?? Path.Combine(Environment.CurrentDirectory, "settings.json");
            var configuration = await ConfigurationLoader.LoadAsync(path, cancellationToken);
            presenter = new ConsolePresenter(console, interactive, configuration);
            using var httpClient = new HttpClient();
            var ipProvider = new PublicIpProvider(httpClient, configuration);
            var dnsClient = new AliDnsClient(httpClient, configuration.AccessId, configuration.AccessKey);
            var updater = new DnsUpdater(configuration, dnsClient, ipProvider);
            var runner = new ApplicationRunner(configuration, updater, ipProvider, presenter,
                System.Console.In, interactive);
            return await runner.RunAsync(options.IntervalSeconds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception exception)
        {
            presenter.WriteError(exception.Message);
            return 1;
        }
    }
}