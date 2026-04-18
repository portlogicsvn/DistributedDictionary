using Microsoft.Extensions.DependencyInjection;
using PLC.Shared.DistributedConcurrentDictionary;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

/// <summary>
/// Covers <see cref="Microsoft.Extensions.DependencyInjection.DistributedDictionaryServiceCollectionExtensions.AddDistributedDictionaryNode"/>
/// and <see cref="IDistributedDictionaryFactory"/> end-to-end (requires Redis).
/// </summary>
public sealed class DistributedDictionaryNodeTests
{
    [SkippableFact]
    public void AddDistributedDictionaryNode_ResolvesFactory_AndMultipleTopicsWork()
    {
        string redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
        string basePrefix = "test:di-node:" + Guid.NewGuid().ToString("N");

        ServiceCollection services = new ServiceCollection();
        services.AddDistributedDictionaryNode(node =>
        {
            node.RedisConnectionString = redisConnection;
            node.ConfigureDistributedDictionary = o =>
            {
                o.KeyPrefix = basePrefix;
            };
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        IDistributedDictionaryFactory factory;
        try
        {
            factory = provider.GetRequiredService<IDistributedDictionaryFactory>();
        }
        catch (RedisConnectionException ex)
        {
            Skip.If(true, "Redis is not available: " + ex.Message);
            throw;
        }

        JsonFusionRedisDistributedDictionary<string, string> a = factory.GetOrAdd<string, string>("topic-a");
        JsonFusionRedisDistributedDictionary<string, string> b = factory.GetOrAdd<string, string>("topic-b");

        a.Clear();
        b.Clear();
        a["k"] = "from-a";
        b["k"] = "from-b";

        Assert.True(a.TryGetValue("k", out string? av));
        Assert.True(b.TryGetValue("k", out string? bv));
        Assert.Equal("from-a", av);
        Assert.Equal("from-b", bv);
    }

    [SkippableFact]
    public void AddDistributedDictionaryNode_WithExistingMultiplexer_DoesNotRequireConnectionString()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        string basePrefix = "test:di-node-mux:" + Guid.NewGuid().ToString("N");

        ServiceCollection services = new ServiceCollection();
        services.AddSingleton<IConnectionMultiplexer>(mux);
        services.AddDistributedDictionaryNode(node =>
        {
            node.ConfigureDistributedDictionary = o =>
            {
                o.KeyPrefix = basePrefix;
            };
        });

        using ServiceProvider provider = services.BuildServiceProvider();
        IDistributedDictionaryFactory factory = provider.GetRequiredService<IDistributedDictionaryFactory>();
        JsonFusionRedisDistributedDictionary<string, string> dict = factory.GetOrAdd<string, string>("solo");
        dict.Clear();
        dict["x"] = "y";
        Assert.True(dict.TryGetValue("x", out string? v));
        Assert.Equal("y", v);
    }
}
