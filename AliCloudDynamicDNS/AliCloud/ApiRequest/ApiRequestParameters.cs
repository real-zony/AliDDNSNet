using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AliCloudDynamicDNS.AliCloud.ApiRequest;

internal static class ApiRequestParameters
{
    internal static string CreateQuery(string accessId, string accessKey,
        IReadOnlyDictionary<string, string> parameters, DateTimeOffset timestamp, string nonce)
    {
        var sorted = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = accessId,
            ["Format"] = "json",
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = nonce,
            ["SignatureVersion"] = "1.0",
            ["Timestamp"] = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["Version"] = "2015-01-09"
        };
        foreach (var parameter in parameters)
            sorted.Add(parameter.Key, parameter.Value);

        var builder = new StringBuilder();
        foreach (var parameter in sorted)
        {
            if (builder.Length != 0)
                builder.Append('&');
            builder.Append(Uri.EscapeDataString(parameter.Key)).Append('=')
                .Append(Uri.EscapeDataString(parameter.Value));
        }

        var query = builder.ToString();
        var stringToSign = "GET&%2F&" + Uri.EscapeDataString(query);
        var signature = HMACSHA1.HashData(Encoding.UTF8.GetBytes(accessKey + "&"), Encoding.UTF8.GetBytes(stringToSign));
        return query + "&Signature=" + Uri.EscapeDataString(Convert.ToBase64String(signature));
    }
}