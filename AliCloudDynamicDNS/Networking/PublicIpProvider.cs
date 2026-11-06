using System.Net;
using System.Net.Sockets;
using AliCloudDynamicDNS.Configuration;

namespace AliCloudDynamicDNS.Networking;

public interface IPublicIpProvider
{
    Task<IPAddress> GetAsync(AddressFamily addressFamily, CancellationToken cancellationToken);
}

public sealed class PublicIpProvider(HttpClient httpClient, ConfigurationModel configuration) : IPublicIpProvider
{
    public async Task<IPAddress> GetAsync(AddressFamily addressFamily, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = addressFamily switch
        {
            AddressFamily.InterNetwork => configuration.PublicIpServer ?? "https://api.ipify.org",
            AddressFamily.InterNetworkV6 => configuration.PublicIpv6Server ?? configuration.PublicIpServer ?? "https://api6.ipify.org",
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily), "Only IPv4 and IPv6 are supported.")
        };

        string content;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Public IP source returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);

            content = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Do not surface arbitrary request URLs from the HTTP stack.
            throw new HttpRequestException(exception.StatusCode is { } status
                ? $"Public IP source returned HTTP {(int)status}."
                : "Public IP source request failed.", null, exception.StatusCode);
        }

        if (!IPAddress.TryParse(content.Trim(), out var address))
            throw new InvalidDataException("Public IP source did not return a valid IP address.");

        if (address.AddressFamily != addressFamily)
        {
            var suggestion = addressFamily == AddressFamily.InterNetworkV6
                ? "Configure PublicIpv6Server with an IPv6 source."
                : "Configure PublicIpServer with an IPv4 source.";
            throw new InvalidDataException($"Public IP source returned the wrong address family. {suggestion}");
        }

        return address;
    }
}
