using System.CommandLine;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using AliCloudDynamicDNS.Cli;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;
using AliCloudDynamicDNS.Runtime;
using AliCloudDynamicDNS.Updating;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Xunit;

namespace AliCloudDynamicDNS.Test;

public sealed class ApplicationRunnerTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private static readonly UpdateResult Success = new(Array.Empty<RecordUpdateResult>());
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SingleRunAwaitsCompletion()
    {
        var updates = new ControlledUpdater();
        var runner = CreateRunner(updates);
        var running = runner.RunAsync(0, TestCancellation);
        var update = await updates.NextAsync();

        Assert.False(running.IsCompleted);
        update.Complete(Success);

        Assert.Equal(0, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(1, updates.Completed);
    }

    [Fact]
    public async Task SingleRunReturnsFailureForFailedRecord()
    {
        var updates = new ControlledUpdater();
        var running = CreateRunner(updates).RunAsync(0, TestCancellation);
        (await updates.NextAsync()).Complete(new UpdateResult(new[]
        {
            new RecordUpdateResult("www", "A", null, null, null, RecordUpdateStatus.Failed, "record missing")
        }));

        Assert.Equal(1, await running.WaitAsync(TestDeadline, TestCancellation));
    }

    [Fact]
    public async Task SerializesManualAndScheduledUpdates()
    {
        var updates = new ControlledUpdater();
        var clock = new ObservedClock();
        var input = new CommandInput();
        var context = new PumpedContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = context.Start(() => CreateRunner(updates, input, true, clock).RunAsync(30, cancellation.Token));
        var startup = await updates.NextAsync();
        await input.NextReadAsync();
        input.Send("update");
        await input.NextDeliveryAsync();
        await context.RunOneAsync();
        context.Drain();
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(1, updates.Started);
        Assert.False(clock.HasDelay);

        startup.Complete(Success);
        await context.RunUntilAsync(() => updates.Started >= 2 && clock.HasDelay);
        var manual = await updates.NextAsync();
        await clock.NextDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await context.RunOneAsync();
        context.Drain();
        Assert.Equal(2, updates.Started);
        manual.Complete(Success);
        await context.RunUntilAsync(() => updates.Started >= 3);
        var scheduled = await updates.NextAsync();
        scheduled.Complete(Success);
        await context.RunUntilAsync(() => clock.HasDelay);
        await clock.NextDelayAsync();
        await input.NextReadAsync();
        input.Send("exit");
        await context.RunUntilAsync(() => running.IsCompleted);

        Assert.Equal(0, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(3, updates.Completed);
        Assert.Equal(1, updates.MaximumConcurrent);
    }

    [Fact]
    public async Task ScheduledDelayStartsAfterUpdateCompletes()
    {
        var updates = new ControlledUpdater();
        var clock = new ObservedClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, clock: clock).RunAsync(30, cancellation.Token);
        var first = await updates.NextAsync();
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(clock.HasDelay);
        first.Complete(Success);
        Assert.Equal(TimeSpan.FromSeconds(30), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, updates.Started);
        clock.Advance(TimeSpan.FromSeconds(1));
        (await updates.NextAsync()).Complete(Success);
        await clock.NextDelayAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(2, updates.Completed);
    }

    [Fact]
    public async Task EofDoesNotStopScheduledUpdates()
    {
        var updates = new ControlledUpdater();
        var clock = new ObservedClock();
        var input = new CommandInput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, input, true, clock).RunAsync(30, cancellation.Token);
        (await updates.NextAsync()).Complete(Success);
        await input.NextReadAsync();
        input.End();
        await input.NextDeliveryAsync();
        await clock.NextDelayAsync();
        Assert.False(running.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(30));
        (await updates.NextAsync()).Complete(Success);
        await clock.NextDelayAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(2, updates.Completed);
        Assert.Equal(1, input.ReadCount);
    }

    [Fact]
    public async Task CancellationInterruptsPendingInputAndNetwork()
    {
        var updates = new ControlledUpdater();
        var input = new CommandInput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, input, true).RunAsync(30, cancellation.Token);
        var network = await updates.NextAsync();
        await input.NextReadAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        await network.Cancelled.Task.WaitAsync(TestDeadline, TestCancellation);
        await input.Cancelled.Task.WaitAsync(TestDeadline, TestCancellation);
        Assert.Equal(0, updates.Completed);
    }

    [Fact]
    public async Task CancellationDoesNotDependOnReaderHonoringItsToken()
    {
        var updates = new ControlledUpdater();
        var input = new UncooperativeInput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, input, true).RunAsync(30, cancellation.Token);
        await updates.NextAsync();
        await input.Entered.Task.WaitAsync(TestDeadline, TestCancellation);
        cancellation.Cancel();
        try
        {
            Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        }
        finally
        {
            input.Release.TrySetResult(null);
        }
    }

    [Fact]
    public async Task CancellationInterruptsSynchronouslyBlockedConsoleRead()
    {
        var updates = new ControlledUpdater();
        using var input = new BlockingInput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, input, true).RunAsync(30, cancellation.Token);
        await updates.NextAsync();
        await input.Entered.Task.WaitAsync(TestDeadline, TestCancellation);
        cancellation.Cancel();
        try
        {
            Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
            Assert.False(input.Returned.Task.IsCompleted);
        }
        finally
        {
            input.Unblock();
            await input.Returned.Task.WaitAsync(TestDeadline, TestCancellation);
        }
    }

    [Fact]
    public async Task ExitCommandCancelsInFlightUpdateAndReturnsZero()
    {
        var updates = new ControlledUpdater();
        var input = new CommandInput();
        var running = CreateRunner(updates, input, true).RunAsync(30, TestCancellation);
        var network = await updates.NextAsync();
        await input.NextReadAsync();
        input.Send(" ExIt ");

        Assert.Equal(0, await running.WaitAsync(TestDeadline, TestCancellation));
        await network.Cancelled.Task.WaitAsync(TestDeadline, TestCancellation);
    }

    [Fact]
    public async Task LargeIntervalDoesNotOverflowOrFireEarly()
    {
        var updates = new ControlledUpdater();
        var clock = new ObservedClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, clock: clock).RunAsync(uint.MaxValue, cancellation.Token);
        (await updates.NextAsync()).Complete(Success);
        var firstDelay = await clock.NextDelayAsync();
        Assert.InRange(firstDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(uint.MaxValue - 1));
        clock.Advance(TimeSpan.FromDays(50));
        await clock.NextDelayAsync();
        Assert.Equal(1, updates.Completed);
        clock.Advance(TimeSpan.FromSeconds(uint.MaxValue) - TimeSpan.FromDays(50) - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await clock.NextDelayAsync());
        Assert.Equal(1, updates.Started);
        clock.Advance(TimeSpan.FromSeconds(1));
        (await updates.NextAsync()).Complete(Success);
        await clock.NextDelayAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(2, updates.Completed);
    }

    [Fact]
    public async Task RecurringFailureDoesNotStopScheduling()
    {
        var updates = new ControlledUpdater();
        var clock = new ObservedClock();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, clock: clock).RunAsync(30, cancellation.Token);
        (await updates.NextAsync()).Complete(new UpdateResult(Array.Empty<RecordUpdateResult>(), "list failed"));
        await clock.NextDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        (await updates.NextAsync()).Complete(Success);
        await clock.NextDelayAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(2, updates.Completed);
    }

    [Theory]
    [InlineData("A", AddressFamily.InterNetwork)]
    [InlineData("AAAA", AddressFamily.InterNetworkV6)]
    public async Task IpCommandQueriesOnlyConfiguredFamilyWithoutUpdatingDns(string type, AddressFamily family)
    {
        var updates = new ControlledUpdater();
        var input = new CommandInput();
        var ip = new RecordingIpProvider();
        var configuration = Configuration(type);
        var output = new StringWriter();
        var runner = new ApplicationRunner(configuration, updates, ip, Presenter(output, configuration), input, true);
        var running = runner.RunAsync(30, TestCancellation);
        (await updates.NextAsync()).Complete(Success);
        await input.NextReadAsync();
        input.Send("IP");
        await input.NextReadAsync();
        input.Send("CONFIG");
        await input.NextReadAsync();
        input.Send("cls");
        await input.NextReadAsync();
        input.Send("exit");

        Assert.Equal(0, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(new[] { family }, ip.RequestedFamilies);
        Assert.Equal(1, updates.Started);
        Assert.Contains(type == "A" ? "192.0.2.10" : "2001:db8::10", output.ToString());
        Assert.Contains("example.test", output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }

    [Fact]
    public async Task NoninteractiveModeDoesNotReadInput()
    {
        var updates = new ControlledUpdater();
        var input = new UncooperativeInput();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var running = CreateRunner(updates, input, false).RunAsync(30, cancellation.Token);
        await updates.NextAsync();
        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.False(input.Entered.Task.IsCompleted);
    }

    [Theory]
    [InlineData("29")]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("not-a-number")]
    public async Task InvalidIntervalNeverStartsApplication(string value)
    {
        var executions = 0;
        var command = CliApplication.CreateRootCommand((_, _) =>
        {
            executions++;
            return Task.FromResult(0);
        });

        var diagnostics = new StringWriter();
        var parsed = command.Parse(new[] { "-i", value });
        Assert.NotEmpty(parsed.Errors);
        Assert.NotEqual(0, await parsed.InvokeAsync(new InvocationConfiguration
        {
            Output = TextWriter.Null,
            Error = diagnostics
        }, TestCancellation));
        Assert.False(string.IsNullOrWhiteSpace(diagnostics.ToString()));
        Assert.DoesNotContain(nameof(InvalidOperationException), diagnostics.ToString());
        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task CultureSpecificPositiveSignCannotBypassMinimumInterval()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.PositiveSign = "p";
        try
        {
            CultureInfo.CurrentCulture = culture;
            var executions = 0;
            var command = CliApplication.CreateRootCommand((_, _) =>
            {
                executions++;
                return Task.FromResult(0);
            });
            var diagnostics = new StringWriter();
            var parsed = command.Parse(new[] { "-i", "p29" });
            var exitCode = await parsed.InvokeAsync(new InvocationConfiguration
            {
                Output = TextWriter.Null,
                Error = diagnostics
            }, TestCancellation).WaitAsync(TestDeadline, TestCancellation);

            Assert.Equal(0, executions);
            Assert.NotEqual(0, exitCode);
            Assert.NotEmpty(parsed.Errors);
            Assert.False(string.IsNullOrWhiteSpace(diagnostics.ToString()));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task HelpDoesNotLoadOrRunApplication()
    {
        var command = CliApplication.CreateRootCommand((_, _) => throw new InvalidOperationException("must not execute"));
        Assert.Equal(0, await command.Parse(new[] { "--help" }).InvokeAsync(cancellationToken: TestCancellation));
    }

    [Fact]
    public async Task DefaultCliInvocationAwaitsActualSingleCycle()
    {
        var updates = new ControlledUpdater();
        var runner = CreateRunner(updates);
        var command = CliApplication.CreateRootCommand((options, token) => runner.RunAsync(options.IntervalSeconds, token));
        var running = command.Parse(Array.Empty<string>()).InvokeAsync(cancellationToken: TestCancellation);
        var network = await updates.NextAsync();
        Assert.False(running.IsCompleted);
        network.Complete(Success);
        Assert.Equal(0, await running.WaitAsync(TestDeadline, TestCancellation));
        Assert.Equal(1, updates.Completed);
    }

    private static ConfigurationModel Configuration(string type = "A") => new()
    {
        AccessId = "test-access-id",
        AccessKey = "test-access-key",
        MainDomain = "example.test",
        SubDomains = new[] { new SubDomainRecord(type, "www", 600) }
    };

    private static ConsolePresenter Presenter(TextWriter output, ConfigurationModel configuration) => new(
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            ColorSystem = ColorSystemSupport.NoColors
        }), false, configuration);

    private static ApplicationRunner CreateRunner(ControlledUpdater updates, TextReader? input = null,
        bool interactive = false, TimeProvider? clock = null)
    {
        var configuration = Configuration();
        return new ApplicationRunner(configuration, updates, new RecordingIpProvider(),
            Presenter(new StringWriter(), configuration), input ?? TextReader.Null, interactive, clock);
    }

    private sealed class RecordingIpProvider : IPublicIpProvider
    {
        public List<AddressFamily> RequestedFamilies { get; } = new();
        public Task<IPAddress> GetAsync(AddressFamily addressFamily, CancellationToken cancellationToken)
        {
            RequestedFamilies.Add(addressFamily);
            return Task.FromResult(IPAddress.Parse(addressFamily == AddressFamily.InterNetwork ? "192.0.2.10" : "2001:db8::10"));
        }
    }

    private sealed class ControlledUpdater : IDnsUpdater
    {
        private readonly Channel<UpdateInvocation> _calls = Channel.CreateUnbounded<UpdateInvocation>();
        private int _active;
        private int _maximum;
        private int _started;
        private int _completed;
        public int Started => Volatile.Read(ref _started);
        public int Completed => Volatile.Read(ref _completed);
        public int MaximumConcurrent => Volatile.Read(ref _maximum);
        public async Task<UpdateResult> UpdateAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = Volatile.Read(ref _maximum); }
            while (active > previous && Interlocked.CompareExchange(ref _maximum, active, previous) != previous);
            Interlocked.Increment(ref _started);
            var invocation = new UpdateInvocation();
            _calls.Writer.TryWrite(invocation);
            try
            {
                var result = await invocation.Result.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref _completed);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                invocation.Cancelled.TrySetResult();
                throw;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public async Task<UpdateInvocation> NextAsync() =>
            await _calls.Reader.ReadAsync(TestCancellation).AsTask().WaitAsync(TestDeadline, TestCancellation);
    }

    private sealed class UpdateInvocation
    {
        public TaskCompletionSource<UpdateResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(UpdateResult result) => Result.SetResult(result);
    }

    private sealed class CommandInput : TextReader
    {
        private readonly Channel<string> _commands = Channel.CreateUnbounded<string>();
        private readonly Channel<bool> _reads = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _deliveries = Channel.CreateUnbounded<bool>();
        private int _readCount;
        public int ReadCount => Volatile.Read(ref _readCount);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Send(string command) => _commands.Writer.TryWrite(command);
        public void End() => _commands.Writer.TryComplete();
        public async Task NextReadAsync() =>
            await _reads.Reader.ReadAsync(TestCancellation).AsTask().WaitAsync(TestDeadline, TestCancellation);
        public async Task NextDeliveryAsync() =>
            await _deliveries.Reader.ReadAsync(TestCancellation).AsTask().WaitAsync(TestDeadline, TestCancellation);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            _reads.Writer.TryWrite(true);
            try
            {
                var line = await _commands.Reader.WaitToReadAsync(cancellationToken)
                    ? await _commands.Reader.ReadAsync(cancellationToken) : null;
                _deliveries.Writer.TryWrite(true);
                return line;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class UncooperativeInput : TextReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return new ValueTask<string?>(Release.Task.WaitAsync(TestCancellation));
        }
    }

    private sealed class BlockingInput : TextReader
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Unblock() => _release.Set();
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            _release.Wait(TestCancellation);
            Returned.TrySetResult();
            return ValueTask.FromResult<string?>(null);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _release.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Pumping queued async continuations makes attempted overlapping updates observable before
    // releasing either network operation; no sleep or scheduler timing decides this test.
    private sealed class PumpedContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _work =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback callback, object? state) => _work.Writer.TryWrite((callback, state));

        public Task<int> Start(Func<Task<int>> action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(previous); }
        }

        public async Task RunOneAsync()
        {
            var work = await _work.Reader.ReadAsync(TestCancellation).AsTask().WaitAsync(TestDeadline, TestCancellation);
            Execute(work);
        }

        public async Task RunUntilAsync(Func<bool> finished)
        {
            while (!finished())
            {
                await RunOneAsync();
            }
            Drain();
        }

        public void Drain()
        {
            while (_work.Reader.TryRead(out var work))
            {
                Execute(work);
            }
        }

        private void Execute((SendOrPostCallback Callback, object? State) work)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { work.Callback(work.State); }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private sealed class ObservedClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();
        public bool HasDelay => _delays.Reader.TryPeek(out _);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
        public async Task<TimeSpan> NextDelayAsync() =>
            await _delays.Reader.ReadAsync(TestCancellation).AsTask().WaitAsync(TestDeadline, TestCancellation);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }
}
