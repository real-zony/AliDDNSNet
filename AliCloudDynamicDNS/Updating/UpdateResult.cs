namespace AliCloudDynamicDNS.Updating;

public enum RecordUpdateStatus
{
    Updated,
    Unchanged,
    Failed
}

public sealed record RecordUpdateResult(
    string SubDomain,
    string Type,
    string? RecordId,
    string? OldValue,
    string? NewValue,
    RecordUpdateStatus Status,
    string? Error);

public sealed record UpdateResult(IReadOnlyList<RecordUpdateResult> Records, string? Error = null)
{
    public bool IsSuccess => Error is null && !Records.Any(record => record.Status == RecordUpdateStatus.Failed);
}
