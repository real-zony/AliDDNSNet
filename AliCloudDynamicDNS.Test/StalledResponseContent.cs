using System.Net;

namespace AliCloudDynamicDNS.Test;

internal sealed class StalledResponseContent(CancellationToken cleanupToken) : HttpContent
{
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cleanupToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
