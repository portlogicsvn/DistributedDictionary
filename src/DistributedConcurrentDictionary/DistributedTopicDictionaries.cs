using System.Collections.Concurrent;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary;

/// <summary>
/// Register shared FusionCache + Redis + RedLock once, then obtain per-topic
/// <see cref="JsonFusionRedisDistributedDictionary{TKey,TValue}"/> instances on demand (lazy, singleton per topic and type pair).
/// Each topic uses <see cref="FusionRedisDictionaryOptions.WithTopic"/> so Redis keys and locks stay isolated.
/// </summary>
public sealed class DistributedTopicDictionaries : IDistributedDictionaryFactory
{
    private readonly IFusionCache _cache;
    private readonly IConnectionMultiplexer _redis;
    private readonly IDistributedLockFactory _lockFactory;
    private readonly FusionRedisDictionaryOptions _template;
    private readonly ConcurrentDictionary<string, object> _instances = new();

    public DistributedTopicDictionaries(
        IFusionCache cache,
        IConnectionMultiplexer redisConnection,
        IDistributedLockFactory lockFactory,
        FusionRedisDictionaryOptions? templateOptions = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(redisConnection);
        ArgumentNullException.ThrowIfNull(lockFactory);
        FusionRedisDictionaryOptions t = templateOptions ?? new FusionRedisDictionaryOptions();
        if (string.IsNullOrWhiteSpace(t.KeyPrefix))
        {
            throw new ArgumentException("Template KeyPrefix must be non-empty.", nameof(templateOptions));
        }

        _cache = cache;
        _redis = redisConnection;
        _lockFactory = lockFactory;
        _template = t;
    }

    /// <summary>Returns the existing dictionary for this topic and type pair, or creates it once.</summary>
    public JsonFusionRedisDistributedDictionary<TKey, TValue> GetOrAdd<TKey, TValue>(string topic)
        where TKey : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        string t = topic.Trim();
        string registryKey = MakeRegistryKey(t, typeof(TKey), typeof(TValue));
        object boxed = _instances.GetOrAdd(
            registryKey,
            _ =>
            {
                FusionRedisDictionaryOptions o = _template.WithTopic(t);
                return new JsonFusionRedisDistributedDictionary<TKey, TValue>(_cache, _redis, _lockFactory, o);
            });
        return (JsonFusionRedisDistributedDictionary<TKey, TValue>)boxed;
    }

    private static string MakeRegistryKey(string topic, Type keyType, Type valueType)
    {
        return string.Concat(topic, "\x1F", keyType.AssemblyQualifiedName, "\x1F", valueType.AssemblyQualifiedName);
    }
}
