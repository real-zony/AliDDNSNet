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
            configuration = await JsonSerializer.DeserializeAsync(stream,
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
