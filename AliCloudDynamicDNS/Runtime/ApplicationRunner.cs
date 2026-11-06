using System.Net.Sockets;
using AliCloudDynamicDNS.Cli;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;
using AliCloudDynamicDNS.Updating;

namespace AliCloudDynamicDNS.Runtime;

public sealed class ApplicationRunner
{
    // Each native timer stays comfortably below the platform's maximum due time.
    private static readonly TimeSpan MaximumDelaySegment = TimeSpan.FromDays(1);
    private readonly ConfigurationModel _configuration;
    private readonly IDnsUpdater _updater;
    private readonly IPublicIpProvider _ipProvider;
    private readonly ConsolePresenter _presenter;
    private readonly TextReader _input;
    private readonly bool _interactive;
    private readonly TimeProvider _timeProvider;

    public ApplicationRunner(ConfigurationModel configuration, IDnsUpdater updater, IPublicIpProvider ipProvider,
        ConsolePresenter presenter, TextReader input, bool interactive, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _updater = updater;
        _ipProvider = ipProvider;
        _presenter = presenter;
        _input = input;
        _interactive = interactive;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> RunAsync(uint intervalSeconds, CancellationToken cancellationToken)
    {
        if (intervalSeconds is > 0 and < 30)
        {
            _presenter.WriteError("自动检测周期必须为 0（单次运行），或大于等于 30 秒。");
            return 1;
        }

        using var updateGate = new SemaphoreSlim(1, 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            _presenter.WriteConfiguration(_configuration, intervalSeconds);
            if (intervalSeconds == 0)
            {
                var succeeded = await UpdateAsync(updateGate, true, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                return succeeded ? 0 : 1;
            }

            if (_interactive)
            {
                _presenter.WriteCommandHelp();
            }

            var scheduled = RunScheduledAsync(intervalSeconds, updateGate, lifetime.Token);
            var commands = _interactive
                ? RunCommandsAsync(intervalSeconds, updateGate, lifetime)
                : Task.CompletedTask;
            // EOF completes only the command loop. The scheduled loop remains alive until exit/cancellation.
            await Task.WhenAll(scheduled, commands);
            return cancellationToken.IsCancellationRequested ? 130 : 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return cancellationToken.IsCancellationRequested ? 130 : 0;
        }
    }

    private async Task RunScheduledAsync(uint intervalSeconds, SemaphoreSlim updateGate, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpdateAsync(updateGate, false, cancellationToken);
            await DelayAsync(interval, cancellationToken);
        }
    }

    private async Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = interval - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining > MaximumDelaySegment ? MaximumDelaySegment : remaining,
                _timeProvider, cancellationToken);
        }
    }

    private async Task<bool> UpdateAsync(SemaphoreSlim updateGate, bool includeUnchanged, CancellationToken cancellationToken)
    {
        await updateGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _updater.UpdateAsync(cancellationToken);
            _presenter.WriteUpdateResult(result, includeUnchanged);
            return result.IsSuccess;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _presenter.WriteError($"更新失败：{exception.Message}");
            return false;
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task RunCommandsAsync(uint intervalSeconds, SemaphoreSlim updateGate, CancellationTokenSource lifetime)
    {
        var cancellationToken = lifetime.Token;
        while (true)
        {
            string? command;
            try
            {
                command = await ReadCommandAsync(cancellationToken);
            }
            catch (IOException exception)
            {
                _presenter.WriteError($"无法继续读取命令：{exception.Message}");
                return;
            }

            if (command is null)
            {
                return;
            }

            switch (command.Trim().ToLowerInvariant())
            {
                case "exit":
                    await lifetime.CancelAsync();
                    return;
                case "update":
                    await UpdateAsync(updateGate, true, cancellationToken);
                    break;
                case "ip":
                    await WriteIpAsync(cancellationToken);
                    break;
                case "config":
                    _presenter.WriteConfiguration(_configuration, intervalSeconds);
                    break;
                case "cls":
                    _presenter.Clear();
                    _presenter.WriteConfiguration(_configuration, intervalSeconds);
                    break;
                case "":
                    break;
                default:
                    _presenter.WriteCommandHelp();
                    break;
            }
        }
    }

    private async Task<string?> ReadCommandAsync(CancellationToken cancellationToken)
    {
        // Console.In may synchronously block even through ReadLineAsync. Isolate that OS boundary;
        // WaitAsync also lets shutdown finish when a TextReader ignores its cancellation token.
        var read = Task.Run(async () => await _input.ReadLineAsync(cancellationToken), cancellationToken);
        try
        {
            return await read.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A non-cancelable console read can outlive shutdown. Observe any later I/O failure.
            _ = read.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private async Task WriteIpAsync(CancellationToken cancellationToken)
    {
        var needsV4 = _configuration.SubDomains.Any(target => target.Type.Equals("A", StringComparison.OrdinalIgnoreCase));
        var needsV6 = _configuration.SubDomains.Any(target => target.Type.Equals("AAAA", StringComparison.OrdinalIgnoreCase));
        if (needsV4)
        {
            await WriteFamilyAsync(AddressFamily.InterNetwork, cancellationToken);
        }
        if (needsV6)
        {
            await WriteFamilyAsync(AddressFamily.InterNetworkV6, cancellationToken);
        }
    }

    private async Task WriteFamilyAsync(AddressFamily family, CancellationToken cancellationToken)
    {
        try
        {
            _presenter.WriteIp(family, await _ipProvider.GetAsync(family, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _presenter.WriteError($"获取 {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} 失败：{exception.Message}");
        }
    }
}
