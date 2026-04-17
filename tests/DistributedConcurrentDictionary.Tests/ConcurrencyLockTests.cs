using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class ConcurrencyLockTests
{
    [SkippableFact]
    public async Task Add_SameKey_FromTwoTasks_ShouldAllowExactlyOneWinner()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:raceadd:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict.Clear();
        string raceKey = "same-key";
        Task<bool> t1 = Task.Run(() =>
        {
            try { dict.Add(raceKey, "from-t1"); return true; }
            catch (ArgumentException) { return false; }
        });
        Task<bool> t2 = Task.Run(() =>
        {
            try { dict.Add(raceKey, "from-t2"); return true; }
            catch (ArgumentException) { return false; }
        });

        bool[] results = await Task.WhenAll(t1, t2);
        int winners = results.Count(static r => r);
        Assert.Equal(1, winners);
        Assert.Single(dict);
        Assert.True(dict.TryGetValue(raceKey, out string? winnerValue));
        Assert.True(winnerValue == "from-t1" || winnerValue == "from-t2");
    }

    [SkippableFact]
    public async Task Lock_Contention_TwoRemoves_SameKey_ShouldBeIdempotent()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:remove-race:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);
        dict["k"] = "v";

        Task<bool> r1 = Task.Run(() => dict.Remove("k"));
        Task<bool> r2 = Task.Run(() => dict.Remove("k"));
        bool[] res = await Task.WhenAll(r1, r2);

        Assert.Single(res, static x => x);
        Assert.False(dict.ContainsKey("k"));
    }

    [SkippableFact]
    public void LockAcquireTimeout_ShouldThrowTimeoutException()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string resource = "test:lock-timeout:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = new JsonFusionRedisDistributedDictionary<string, string>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = resource,
                LockExpiry = TimeSpan.FromSeconds(5),
                LockWait = TimeSpan.Zero,
                LockRetry = TimeSpan.Zero,
            });

        string encodedKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("\"k1\""));
        string lockResource = resource + ":lck:k:" + encodedKey;
        using IRedLock held = lockFactory.CreateLock(lockResource, TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.Zero);
        Assert.True(held.IsAcquired);

        Assert.Throws<TimeoutException>(() => dict.Add("k1", "v1"));
    }
}
