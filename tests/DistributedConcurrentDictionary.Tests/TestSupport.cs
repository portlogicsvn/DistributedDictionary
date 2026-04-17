using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

internal static class TestSupport
{
    internal static ConnectionMultiplexer ConnectOrSkip()
    {
        string redisConnection = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "localhost:6379";
        try
        {
            return ConnectionMultiplexer.Connect(redisConnection);
        }
        catch (RedisConnectionException ex)
        {
            Skip.If(true, "Redis is not available for integration tests: " + ex.Message);
            throw;
        }
    }

    internal static IDistributedLockFactory CreateLockFactory(ConnectionMultiplexer mux)
    {
        return RedLockFactory.Create(new[] { new RedLockMultiplexer(mux) });
    }

    internal static JsonFusionRedisDistributedDictionary<string, string> CreateStringDictionary(
        FusionCache cache,
        ConnectionMultiplexer mux,
        IDistributedLockFactory lockFactory,
        string prefix)
    {
        return new JsonFusionRedisDistributedDictionary<string, string>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = prefix,
            });
    }
}

internal sealed record LaneKey(string Service, string LaneNo);

internal sealed class LaneInfo
{
    public string LaneNo { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;
}
