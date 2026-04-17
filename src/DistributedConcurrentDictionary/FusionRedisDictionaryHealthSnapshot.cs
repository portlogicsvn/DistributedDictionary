namespace PLC.Shared.DistributedConcurrentDictionary;

public sealed class FusionRedisDictionaryHealthSnapshot
{
    public bool IsDegraded { get; init; }

    public bool IsCircuitOpen { get; init; }

    public DateTimeOffset? CircuitOpenUntilUtc { get; init; }

    public long RedisFailureCount { get; init; }

    public long LockFailureCount { get; init; }

    public DateTimeOffset? LastFailureUtc { get; init; }

    public string? LastFailureMessage { get; init; }
}
