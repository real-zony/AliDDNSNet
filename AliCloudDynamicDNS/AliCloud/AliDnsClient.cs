using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AliCloudDynamicDNS.AliCloud.ApiRequest;
using AliCloudDynamicDNS.AliCloud.Models;
using AliCloudDynamicDNS.Configuration;

namespace AliCloudDynamicDNS.AliCloud;

public interface IAliDnsClient
{
    Task<IReadOnlyList<AliCloudRecordModel>> GetRecordsAsync(string mainDomain, CancellationToken cancellationToken);
    Task UpdateRecordAsync(AliCloudRecordModel record, IPAddress address, SubDomainRecord target, CancellationToken cancellationToken);
}

public sealed class AliDnsClient(HttpClient httpClient, string accessId, string accessKey) : IAliDnsClient
{
    public async Task<IReadOnlyList<AliCloudRecordModel>> GetRecordsAsync(string mainDomain, CancellationToken cancellationToken)
    {
        var records = new List<AliCloudRecordModel>();
        var recordIds = new HashSet<string>(StringComparer.Ordinal);
        int? totalCount = null;
        for (var pageNumber = 1; ; pageNumber++)
        {
            var parameters = new Dictionary<string, string>
            {
                ["Action"] = "DescribeDomainRecords",
                ["DomainName"] = mainDomain,
                ["PageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture),
                ["PageSize"] = "500"
            };
            var page = await RequestAsync(parameters, AliDnsJsonContext.Default.AliDnsListResponse, cancellationToken);
            if (page.TotalCount is null or < 0 || page.PageNumber != pageNumber || page.PageSize is null or <= 0
                || page.DomainRecords?.Record is null)
            {
                throw new InvalidDataException("AliDNS returned invalid record-list pagination metadata.");
            }

            totalCount ??= page.TotalCount.Value;
            if (page.TotalCount.Value != totalCount.Value)
                throw new InvalidDataException("AliDNS record count changed while reading pages; the list is incomplete.");

            foreach (var record in page.DomainRecords.Record)
            {
                if (record is null || string.IsNullOrWhiteSpace(record.RR) || string.IsNullOrWhiteSpace(record.RecordId)
                    || record.Value is null || string.IsNullOrWhiteSpace(record.Type) || string.IsNullOrWhiteSpace(record.Line))
                {
                    throw new InvalidDataException("AliDNS returned a record missing RR, RecordId, Value, Type or Line.");
                }

                if (!recordIds.Add(record.RecordId))
                    throw new InvalidDataException("AliDNS repeated a RecordId while reading pages; the list is incomplete.");

                records.Add(new AliCloudRecordModel(record.RR, record.RecordId, record.Value, record.Type, record.Line));
            }

            if (records.Count > totalCount.Value)
                throw new InvalidDataException("AliDNS returned more records than TotalCount.");
            if (records.Count == totalCount.Value)
                return records.AsReadOnly();
            if (page.DomainRecords.Record.Count == 0 || pageNumber == int.MaxValue)
                throw new InvalidDataException("AliDNS returned a page without progress before TotalCount was reached.");
        }
    }

    public async Task UpdateRecordAsync(AliCloudRecordModel record, IPAddress address, SubDomainRecord target,
        CancellationToken cancellationToken)
    {
        var type = target.Type.ToUpperInvariant();
        if ((type != "A" || address.AddressFamily != AddressFamily.InterNetwork)
            && (type != "AAAA" || address.AddressFamily != AddressFamily.InterNetworkV6))
        {
            throw new InvalidDataException("AliDNS update requires an A/IPv4 or AAAA/IPv6 target.");
        }
        if (string.IsNullOrWhiteSpace(record.RecordId) || string.IsNullOrWhiteSpace(record.Line))
            throw new InvalidDataException("AliDNS update requires the existing RecordId and Line.");

        var parameters = new Dictionary<string, string>
        {
            ["Action"] = "UpdateDomainRecord",
            ["RecordId"] = record.RecordId,
            ["RR"] = target.SubDomain,
            ["Type"] = type,
            ["Value"] = address.ToString(),
            ["TTL"] = target.Interval.ToString(CultureInfo.InvariantCulture),
            ["Line"] = record.Line
        };
        var response = await RequestAsync(parameters, AliDnsJsonContext.Default.AliDnsUpdateResponse, cancellationToken);
        if (!string.Equals(response.RecordId, record.RecordId, StringComparison.Ordinal))
            throw new InvalidDataException("AliDNS update response contains a missing or mismatched RecordId.");
    }

    private async Task<T> RequestAsync<T>(IReadOnlyDictionary<string, string> parameters, JsonTypeInfo<T> responseType,
        CancellationToken cancellationToken) where T : AliDnsErrorResponse
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = ApiRequestParameters.CreateQuery(accessId, accessKey, parameters, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://alidns.aliyuncs.com/?" + query);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            T? result;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                result = await JsonSerializer.DeserializeAsync(stream, responseType, cancellationToken);
            }
            catch (JsonException)
            {
                if (!response.IsSuccessStatusCode)
                    throw HttpFailure(response.StatusCode);
                throw new InvalidDataException("AliDNS returned an invalid JSON response.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(result?.Code))
            {
                throw new InvalidDataException($"AliDNS API error {SafeDiagnostic(result.Code)}: {SafeDiagnostic(result.Message)} "
                    + $"(RequestId: {SafeDiagnostic(result.RequestId)}).");
            }
            if (!response.IsSuccessStatusCode)
                throw HttpFailure(response.StatusCode);
            if (result is null)
                throw new InvalidDataException("AliDNS returned an empty response.");
            return result;
        }
        catch (HttpRequestException exception)
        {
            // HTTP exceptions may include the signed URL. Never retain their message or inner exception.
            throw exception.StatusCode is { } status
                ? HttpFailure(status)
                : new HttpRequestException("AliDNS HTTP request failed.");
        }
    }

    private static HttpRequestException HttpFailure(HttpStatusCode status) =>
        new($"AliDNS returned HTTP {(int)status}.", null, status);

    private string SafeDiagnostic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "not provided";

        // API messages can echo a request URI. Remove URLs before including any server-provided diagnostic.
        var start = 0;
        while (start < text.Length)
        {
            var http = text.IndexOf("http://", start, StringComparison.OrdinalIgnoreCase);
            var https = text.IndexOf("https://", start, StringComparison.OrdinalIgnoreCase);
            var index = http < 0 ? https : https < 0 ? http : Math.Min(http, https);
            if (index < 0)
                break;
            var end = index;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            text = string.Concat(text.AsSpan(0, index), "[redacted URL]", text.AsSpan(end));
            start = index + "[redacted URL]".Length;
        }
        if (!string.IsNullOrEmpty(accessKey))
            text = text.Replace(accessKey, "[redacted]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(accessId))
            text = text.Replace(accessId, "[redacted]", StringComparison.Ordinal);
        return text;
    }
}
