using System.Text.Json.Serialization;

namespace AliCloudDynamicDNS.AliCloud;

internal class AliDnsErrorResponse
{
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? RequestId { get; init; }
}

internal sealed class AliDnsListResponse : AliDnsErrorResponse
{
    public int? TotalCount { get; init; }
    public int? PageNumber { get; init; }
    public int? PageSize { get; init; }
    public AliDnsRecordCollection? DomainRecords { get; init; }
}

internal sealed class AliDnsRecordCollection
{
    public List<AliDnsRecordResponse?>? Record { get; init; }
}

internal sealed class AliDnsRecordResponse
{
    public string? RR { get; init; }
    public string? RecordId { get; init; }
    public string? Value { get; init; }
    public string? Type { get; init; }
    public string? Line { get; init; }
}

internal sealed class AliDnsUpdateResponse : AliDnsErrorResponse
{
    public string? RecordId { get; init; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AliDnsListResponse))]
[JsonSerializable(typeof(AliDnsUpdateResponse))]
[JsonSerializable(typeof(AliDnsErrorResponse))]
internal partial class AliDnsJsonContext : JsonSerializerContext;
