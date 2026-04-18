using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class TopicRegistryTests
{
    [SkippableFact]
    public void GetOrAdd_SameTopicSameTypes_ReturnsSameInstance()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory locks = TestSupport.CreateLockFactory(mux);
        string basePrefix = "test:topics:" + Guid.NewGuid().ToString("N");
        DistributedTopicDictionaries hub = new DistributedTopicDictionaries(
            cache,
            mux,
            locks,
            new FusionRedisDictionaryOptions { KeyPrefix = basePrefix });

        JsonFusionRedisDistributedDictionary<string, string> a = hub.GetOrAdd<string, string>("orders");
        JsonFusionRedisDistributedDictionary<string, string> b = hub.GetOrAdd<string, string>("orders");
        Assert.Same(a, b);
    }

    [SkippableFact]
    public void GetOrAdd_DifferentTopics_ReturnsDifferentInstances()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory locks = TestSupport.CreateLockFactory(mux);
        string basePrefix = "test:topics:" + Guid.NewGuid().ToString("N");
        DistributedTopicDictionaries hub = new DistributedTopicDictionaries(
            cache,
            mux,
            locks,
            new FusionRedisDictionaryOptions { KeyPrefix = basePrefix });

        JsonFusionRedisDistributedDictionary<string, string> a = hub.GetOrAdd<string, string>("orders");
        JsonFusionRedisDistributedDictionary<string, string> b = hub.GetOrAdd<string, string>("invoices");
        Assert.NotSame(a, b);
    }

    [SkippableFact]
    public void GetOrAdd_IsolatesRedisKeyspace_PerTopic()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory locks = TestSupport.CreateLockFactory(mux);
        string basePrefix = "test:topics:" + Guid.NewGuid().ToString("N");
        DistributedTopicDictionaries hub = new DistributedTopicDictionaries(
            cache,
            mux,
            locks,
            new FusionRedisDictionaryOptions { KeyPrefix = basePrefix });

        JsonFusionRedisDistributedDictionary<string, string> orders = hub.GetOrAdd<string, string>("orders");
        JsonFusionRedisDistributedDictionary<string, string> invoices = hub.GetOrAdd<string, string>("invoices");

        orders["id"] = "order-1";
        invoices["id"] = "inv-1";

        Assert.True(orders.TryGetValue("id", out string? ov));
        Assert.True(invoices.TryGetValue("id", out string? iv));
        Assert.Equal("order-1", ov);
        Assert.Equal("inv-1", iv);
    }
}
