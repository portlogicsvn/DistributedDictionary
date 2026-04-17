using System.Text.Json;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary;

public enum FusionRedisFailureMode
{
    FailFast = 0,
    ReadOnlyStale = 1,
}

public enum TosDataCriticality
{
    Critical = 0,
    Operational = 1,
    Reference = 2,
}

public enum FusionRedisEventType
{
    ReadSuccess = 0,
    ReadMiss = 1,
    WriteSuccess = 2,
    RemoveSuccess = 3,
    LockAcquireFailure = 4,
    RedisFailure = 5,
    CircuitOpened = 6,
    CircuitBlocked = 7,
    DegradedRead = 8,
}

public sealed class FusionRedisTelemetryEvent
{
    public FusionRedisEventType EventType { get; init; }

    public string Operation { get; init; } = string.Empty;

    public string? Key { get; init; }

    public TimeSpan Duration { get; init; }

    public Exception? Exception { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Options for <see cref="JsonFusionRedisDistributedDictionary{TKey,TValue}"/>: Redis key prefix, RedLock timings, FusionCache entry defaults, JSON serializer options.
/// </summary>
public sealed class FusionRedisDictionaryOptions
{
    /// <summary>Namespace prefix for Redis keys and lock resources. Non-empty; avoid spaces.</summary>
    public string KeyPrefix { get; set; } = "dcd";

    /// <summary>Lock TTL on Redis; must exceed worst-case write duration.</summary>
    public TimeSpan LockExpiry { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Total wait before failing to acquire a lock.</summary>
    public TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delay between lock retries.</summary>
    public TimeSpan LockRetry { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Default FusionCache entry options for writes (TTL, fail-safe, etc.).</summary>
    public FusionCacheEntryOptions? DefaultEntryOptions { get; set; }

    /// <summary>System.Text.Json options for serializing dictionary keys and values.</summary>
    public JsonSerializerOptions? JsonOptions { get; set; }

    /// <summary>Failure behavior when Redis/lock infrastructure is unhealthy.</summary>
    public FusionRedisFailureMode FailureMode { get; set; } = FusionRedisFailureMode.FailFast;

    /// <summary>Business criticality to drive default resilience profile for TOS services.</summary>
    public TosDataCriticality DataCriticality { get; set; } = TosDataCriticality.Operational;

    /// <summary>Retry attempts for Redis operations before failing.</summary>
    public int RedisRetryCount { get; set; } = 2;

    /// <summary>Delay between Redis retries.</summary>
    public TimeSpan RedisRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Consecutive failures before opening circuit breaker.</summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>Duration for open circuit state; operations fail fast during this window.</summary>
    public TimeSpan CircuitBreakerOpenDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Optional telemetry callback for lock/cache/redis events.</summary>
    public Action<FusionRedisTelemetryEvent>? OnTelemetry { get; set; }
}
