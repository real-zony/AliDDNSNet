namespace AliCloudDynamicDNS.Configuration;

public sealed class ConfigurationModel
{
    public string AccessId { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string MainDomain { get; init; } = string.Empty;

    public string? PublicIpServer { get; init; }

    public string? PublicIpv6Server { get; init; }

    public IReadOnlyList<SubDomainRecord> SubDomains { get; init; } = Array.Empty<SubDomainRecord>();
}