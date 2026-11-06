using System.Diagnostics;
using System.IO.Pipes;

namespace AliCloudDynamicDNS.Test;

internal sealed class NonSeekableConfigurationFile : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("alidns-pipe-config-").FullName;
    private readonly CancellationTokenSource _cancellation;
    private NamedPipeServerStream? _pipe;
    private Task? _writer;
    private FileStream? _fifoWriter;

    internal string Path { get; private set; } = string.Empty;

    private NonSeekableConfigurationFile(CancellationToken cancellationToken) =>
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    internal static async Task<NonSeekableConfigurationFile> CreateAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var file = new NonSeekableConfigurationFile(cancellationToken);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipeName = $"alidns-config-{Guid.NewGuid():N}";
                file.Path = @"\\.\pipe\" + pipeName;
                file._pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 4096);
                file._writer = WriteNamedPipeAsync(file._pipe, bytes, file._cancellation.Token);
            }
            else
            {
                file.Path = System.IO.Path.Combine(file._directory, "settings.fifo");
                var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false, RedirectStandardError = true };
                start.ArgumentList.Add(file.Path);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mkfifo.");
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"mkfifo failed with exit code {process.ExitCode}.");

                // Holding both ends lets the small fixture be queued before the real reader opens,
                // without a blocked helper thread or process. Close the writer after LoadAsync starts.
                file._fifoWriter = new FileStream(file.Path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite, bufferSize: 4096, FileOptions.Asynchronous);
                await file._fifoWriter.WriteAsync(bytes, cancellationToken);
                await file._fifoWriter.FlushAsync(cancellationToken);
            }
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    internal async Task CompleteWritingAsync()
    {
        if (_fifoWriter is not null)
        {
            await _fifoWriter.DisposeAsync();
            _fifoWriter = null;
        }
    }

    private static async Task WriteNamedPipeAsync(NamedPipeServerStream pipe, byte[] bytes, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            await pipe.WriteAsync(bytes, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_pipe is not null)
            await _pipe.DisposeAsync();
        await CompleteWritingAsync();
        if (_writer is not null)
        {
            try { await _writer.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
        _cancellation.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
