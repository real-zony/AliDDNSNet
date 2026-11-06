using System.Text;
using System.Text.Json;

namespace AliCloudDynamicDNS.Configuration;

public static class ConfigurationLoader
{
    public static async Task<ConfigurationModel> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        ConfigurationModel? configuration;
        try
        {
            var prefix = new byte[4];
            var prefixLength = await stream.ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false,
                cancellationToken: cancellationToken);
            var (encoding, preambleLength) = DetectBomEncoding(prefix.AsSpan(0, prefixLength));
            var canSeek = stream.CanSeek;
            if (canSeek)
                stream.Position = preambleLength;
            await using Stream? replay = canSeek ? null
                : new PrefixReadStream(stream, prefix.AsMemory(preambleLength, prefixLength - preambleLength));
            await using var transcoded = encoding is null ? null
                : Encoding.CreateTranscodingStream(replay ?? stream, encoding, Encoding.UTF8, leaveOpen: true);
            configuration = await JsonSerializer.DeserializeAsync(transcoded ?? replay ?? stream,
                ConfigurationJsonContext.Default.ConfigurationModel, cancellationToken);
        }
        catch (JsonException exception)
        {
            // Keep only a recognized field path, never JSON content or the original exception.
            throw new InvalidDataException($"Invalid JSON or field value at {JsonField(exception.Path)}.");
        }

        if (configuration is null)
            throw new InvalidDataException("Configuration must contain a JSON object.");

        RequireValue(configuration.AccessId, "AccessId");
        RequireValue(configuration.AccessKey, "AccessKey");
        RequireValue(configuration.MainDomain, "MainDomain");
        ValidateSource(configuration.PublicIpServer, "PublicIpServer");
        ValidateSource(configuration.PublicIpv6Server, "PublicIpv6Server");

        if (configuration.SubDomains is null || configuration.SubDomains.Count == 0)
            throw new InvalidDataException("SubDomains must contain at least one target.");

        var targets = new SubDomainRecord[configuration.SubDomains.Count];
        for (var index = 0; index < targets.Length; index++)
        {
            var target = configuration.SubDomains[index];
            if (target is null)
                throw new InvalidDataException($"SubDomains[{index}] must be an object.");

            RequireValue(target.SubDomain, $"SubDomains[{index}].SubDomain");
            var type = target.Type?.ToUpperInvariant();
            if (type is not ("A" or "AAAA"))
                throw new InvalidDataException($"SubDomains[{index}].Type must be A or AAAA.");

            targets[index] = target.Type == type ? target : target with { Type = type };
        }

        return new ConfigurationModel
        {
            AccessId = configuration.AccessId,
            AccessKey = configuration.AccessKey,
            MainDomain = configuration.MainDomain,
            PublicIpServer = configuration.PublicIpServer,
            PublicIpv6Server = configuration.PublicIpv6Server,
            SubDomains = Array.AsReadOnly(targets)
        };
    }

    private static (Encoding? Encoding, int PreambleLength) DetectBomEncoding(ReadOnlySpan<byte> prefix)
    {
        var length = prefix.Length;
        // UTF-32 LE starts with the UTF-16 LE BOM, so inspect both four-byte BOMs first.
        if (length >= 4)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE && prefix[2] == 0x00 && prefix[3] == 0x00)
                return (Encoding.UTF32, 4);
            if (prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xFE && prefix[3] == 0xFF)
                return (Encoding.GetEncoding(12001), 4); // Built-in UTF-32 BE; no code-page provider required.
        }
        if (length >= 2)
        {
            if (prefix[0] == 0xFF && prefix[1] == 0xFE)
                return (Encoding.Unicode, 2);
            if (prefix[0] == 0xFE && prefix[1] == 0xFF)
                return (Encoding.BigEndianUnicode, 2);
        }
        if (length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            return (null, 3);

        return (null, 0);
    }

    private sealed class PrefixReadStream(Stream inner, ReadOnlyMemory<byte> prefix) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly ReadOnlyMemory<byte> _prefix = prefix;
        private int _consumed;
        private bool _disposed;

        public override bool CanRead => !_disposed && _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.IsEmpty)
                return 0;
            return _consumed < _prefix.Length ? ReadPrefix(buffer) : _inner.Read(buffer);
        }

        public override int ReadByte()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _consumed < _prefix.Length ? _prefix.Span[_consumed++] : _inner.ReadByte();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<int>(cancellationToken);
            if (buffer.IsEmpty)
                return ValueTask.FromResult(0);
            return _consumed < _prefix.Length
                ? ValueTask.FromResult(ReadPrefix(buffer.Span))
                : _inner.ReadAsync(buffer, cancellationToken);
        }

        private int ReadPrefix(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _consumed);
            _prefix.Span.Slice(_consumed, count).CopyTo(buffer);
            _consumed += count;
            return count;
        }

        public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The loader's outer FileStream scope owns the underlying stream.
            _disposed = true;
            base.Dispose(disposing);
        }
    }

    private static void RequireValue(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{field} must not be empty.");
    }

    private static string JsonField(string? path)
    {
        var normalized = path?.ToUpperInvariant();
        var field = normalized switch
        {
            "$.ACCESSID" => "AccessId",
            "$.ACCESSKEY" => "AccessKey",
            "$.MAINDOMAIN" => "MainDomain",
            "$.PUBLICIPSERVER" => "PublicIpServer",
            "$.PUBLICIPV6SERVER" => "PublicIpv6Server",
            "$.SUBDOMAINS" => "SubDomains",
            _ => null
        };
        if (field is not null)
            return field;

        const string prefix = "$.SUBDOMAINS[";
        if (normalized is not null && normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            var end = normalized.IndexOf(']', prefix.Length);
            if (end > prefix.Length && int.TryParse(normalized.AsSpan(prefix.Length, end - prefix.Length), out var index))
            {
                var suffix = normalized[(end + 1)..] switch
                {
                    "" => "",
                    ".TYPE" => ".Type",
                    ".SUBDOMAIN" => ".SubDomain",
                    ".INTERVAL" => ".Interval",
                    _ => null
                };
                if (suffix is not null)
                    return $"SubDomains[{index}]{suffix}";
            }
        }
        return "configuration";
    }

    private static void ValidateSource(string? value, string field)
    {
        if (value is null)
            return;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidDataException($"{field} must be an absolute HTTP or HTTPS URI.");
        }
    }
}
