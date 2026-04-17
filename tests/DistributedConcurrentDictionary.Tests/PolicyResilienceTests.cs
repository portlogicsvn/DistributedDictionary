using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class PolicyResilienceTests
{
    [SkippableFact]
    public void RedisFailure_ShouldOpenCircuit_AndExposeHealthSnapshot()
    {
        ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:circuit:" + Guid.NewGuid().ToString("N");
        List<FusionRedisEventType> events = new List<FusionRedisEventType>();

        JsonFusionRedisDistributedDictionary<string, string> dict = new JsonFusionRedisDistributedDictionary<string, string>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = prefix,
                FailureMode = FusionRedisFailureMode.FailFast,
                DataCriticality = TosDataCriticality.Critical,
                RedisRetryCount = 0,
                CircuitBreakerFailureThreshold = 1,
                CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30),
                OnTelemetry = e => events.Add(e.EventType),
            });

        dict["probe"] = "ok";
        mux.Dispose();

        Assert.Throws<InvalidOperationException>(() => _ = dict.Count);
        FusionRedisDictionaryHealthSnapshot snapshot = dict.GetHealthSnapshot();
        Assert.True(snapshot.IsCircuitOpen);
        Assert.True(snapshot.RedisFailureCount >= 1);
        Assert.Contains(FusionRedisEventType.RedisFailure, events);
        Assert.Contains(FusionRedisEventType.CircuitOpened, events);
    }

    [SkippableFact]
    public void ReadOnlyStale_Mode_ShouldKeepServingCachedValue_WhenRedisIsDown()
    {
        ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:readonlystale:" + Guid.NewGuid().ToString("N");
        List<FusionRedisEventType> events = new List<FusionRedisEventType>();

        JsonFusionRedisDistributedDictionary<string, string> dict = new JsonFusionRedisDistributedDictionary<string, string>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = prefix,
                FailureMode = FusionRedisFailureMode.ReadOnlyStale,
                DataCriticality = TosDataCriticality.Operational,
                RedisRetryCount = 0,
                CircuitBreakerFailureThreshold = 1,
                CircuitBreakerOpenDuration = TimeSpan.FromSeconds(30),
                OnTelemetry = e => events.Add(e.EventType),
            });

        dict["probe"] = "stale-ok";
        mux.Dispose();

        bool found = dict.TryGetValue("probe", out string? value);
        Assert.True(found);
        Assert.Equal("stale-ok", value);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => _ = dict.Count);
        Assert.Contains("Redis operation failed", ex.Message);
        Assert.Contains(FusionRedisEventType.ReadSuccess, events);
        Assert.Contains(FusionRedisEventType.RedisFailure, events);
    }

    [SkippableFact]
    public void Circuit_Open_ShouldRecover_AfterOpenDuration()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:circuit-recover:" + Guid.NewGuid().ToString("N");

        JsonFusionRedisDistributedDictionary<string, string> dict = new JsonFusionRedisDistributedDictionary<string, string>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = prefix,
                FailureMode = FusionRedisFailureMode.FailFast,
                RedisRetryCount = 0,
                CircuitBreakerFailureThreshold = 1,
                CircuitBreakerOpenDuration = TimeSpan.FromMilliseconds(200),
            });

        dict["k1"] = "v1";
        Thread.Sleep(250);
        Assert.Equal("v1", dict["k1"]);
    }
}
