using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary;

/// <summary>
/// Configuration for <c>AddDistributedDictionaryNode</c>: Redis wiring, FusionCache profile, and template dictionary options (KeyPrefix, locks, resilience, telemetry).
/// </summary>
public sealed class DictionaryDistributedNodeOptions
{
    /// <summary>
    /// Redis connection string. When set, an <see cref="StackExchange.Redis.IConnectionMultiplexer"/> is registered with <c>TryAddSingleton</c> if not already present.
    /// If omitted, you must register <see cref="StackExchange.Redis.IConnectionMultiplexer"/> yourself before calling <c>AddDistributedDictionaryNode</c>.
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Optional FusionCache setup (TTL, plugins, etc.). Assign a lambda, e.g. <c>opts.ConfigureFusionCacheOptions = o => { ... };</c></summary>
    public Action<FusionCacheOptions>? ConfigureFusionCacheOptions { get; set; }

    /// <summary>
    /// Template passed to <see cref="DistributedTopicDictionaries"/> (base <see cref="FusionRedisDictionaryOptions.KeyPrefix"/>, lock timings, <see cref="FusionRedisDictionaryOptions.FailureMode"/>, telemetry, …).
    /// Each topic appends a segment via <see cref="FusionRedisDictionaryOptions.WithTopic"/>.
    /// Assign a lambda, e.g. <c>opts.ConfigureDistributedDictionary = o => { ... };</c>
    /// </summary>
    public Action<FusionRedisDictionaryOptions>? ConfigureDistributedDictionary { get; set; }
}
