using System.Net;
using System.Net.Sockets;
using AliCloudDynamicDNS.AliCloud;
using AliCloudDynamicDNS.AliCloud.Models;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Networking;

namespace AliCloudDynamicDNS.Updating;

public interface IDnsUpdater
{
    Task<UpdateResult> UpdateAsync(CancellationToken cancellationToken);
}

public sealed class DnsUpdater(ConfigurationModel configuration, IAliDnsClient dnsClient, IPublicIpProvider ipProvider) : IDnsUpdater
{
    public async Task<UpdateResult> UpdateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (configuration.SubDomains is null || configuration.SubDomains.Count == 0)
            return new UpdateResult(Array.Empty<RecordUpdateResult>(), "SubDomains must contain at least one target.");

        for (var index = 0; index < configuration.SubDomains.Count; index++)
        {
            var target = configuration.SubDomains[index];
            if (target is null || (!string.Equals(target.Type, "A", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(target.Type, "AAAA", StringComparison.OrdinalIgnoreCase)))
            {
                return new UpdateResult(Array.Empty<RecordUpdateResult>(), $"SubDomains[{index}].Type must be A or AAAA.");
            }
        }

        IReadOnlyList<AliCloudRecordModel> records;
        try
        {
            records = await dnsClient.GetRecordsAsync(configuration.MainDomain, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new UpdateResult(Array.Empty<RecordUpdateResult>(), exception.Message);
        }

        var addresses = new Dictionary<AddressFamily, (IPAddress? Address, string? Error)>();
        foreach (var target in configuration.SubDomains)
        {
            var family = FamilyFor(target.Type);
            if (addresses.ContainsKey(family))
                continue;
            try
            {
                var address = await ipProvider.GetAsync(family, cancellationToken);
                if (address.AddressFamily != family)
                    throw new InvalidDataException("Public IP provider returned the wrong address family.");
                addresses.Add(family, (address, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                addresses.Add(family, (null, exception.Message));
            }
        }

        var lookup = records.ToLookup(record => (record.SubName, record.Type), RecordIdentityComparer.Instance);
        var results = new List<RecordUpdateResult>(configuration.SubDomains.Count);
        foreach (var configuredTarget in configuration.SubDomains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = configuredTarget.Type is "A" or "AAAA"
                ? configuredTarget : configuredTarget with { Type = configuredTarget.Type.ToUpperInvariant() };
            var addressResult = addresses[FamilyFor(target.Type)];
            var newValue = addressResult.Address?.ToString();
            var matches = lookup[(target.SubDomain, target.Type)];
            using var candidates = matches.GetEnumerator();
            if (!candidates.MoveNext())
            {
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, null, null, newValue, RecordUpdateStatus.Failed,
                    $"No remote record found for {target.SubDomain}.{configuration.MainDomain} ({target.Type})."));
                continue;
            }
            var record = candidates.Current;
            if (candidates.MoveNext())
            {
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, null, null, newValue, RecordUpdateStatus.Failed,
                    $"Remote record is ambiguous for {target.SubDomain}.{configuration.MainDomain} ({target.Type})."));
                continue;
            }
            if (addressResult.Address is null)
            {
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, record.RecordId, record.Value, null,
                    RecordUpdateStatus.Failed, addressResult.Error));
                continue;
            }
            if (IPAddress.TryParse(record.Value, out var oldAddress) && oldAddress.Equals(addressResult.Address))
            {
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, record.RecordId, record.Value, newValue,
                    RecordUpdateStatus.Unchanged, null));
                continue;
            }

            try
            {
                await dnsClient.UpdateRecordAsync(record, addressResult.Address, target, cancellationToken);
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, record.RecordId, record.Value, newValue,
                    RecordUpdateStatus.Updated, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new RecordUpdateResult(target.SubDomain, target.Type, record.RecordId, record.Value, newValue,
                    RecordUpdateStatus.Failed, exception.Message));
            }
        }

        return new UpdateResult(results.AsReadOnly());
    }

    private static AddressFamily FamilyFor(string type) => string.Equals(type, "A", StringComparison.OrdinalIgnoreCase)
        ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;

    private sealed class RecordIdentityComparer : IEqualityComparer<(string Name, string Type)>
    {
        internal static readonly RecordIdentityComparer Instance = new();

        public bool Equals((string Name, string Type) left, (string Name, string Type) right) =>
            StringComparer.OrdinalIgnoreCase.Equals(left.Name, right.Name)
            && StringComparer.OrdinalIgnoreCase.Equals(left.Type, right.Type);

        public int GetHashCode((string Name, string Type) identity) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Name), StringComparer.OrdinalIgnoreCase.GetHashCode(identity.Type));
    }
}
