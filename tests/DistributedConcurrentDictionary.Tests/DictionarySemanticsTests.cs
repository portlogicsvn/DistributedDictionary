using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class DictionarySemanticsTests
{
    [SkippableFact]
    public void Crud_MultiKey_ShouldWork()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:crud:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict.Clear();
        dict["k1"] = "v1";
        dict["k2"] = "v2";
        dict["k3"] = "v3";

        Assert.Equal(3, dict.Count);
        Assert.True(dict.ContainsKey("k1"));
        Assert.True(dict.TryGetValue("k2", out string? v2));
        Assert.Equal("v2", v2);

        bool removed = dict.Remove("k2");
        Assert.True(removed);
        Assert.False(dict.ContainsKey("k2"));
        Assert.Equal(2, dict.Count);
    }

    [SkippableFact]
    public void Add_DuplicateKey_SingleThread_ShouldThrow()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:dup:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict.Add("k", "v1");
        Assert.Throws<ArgumentException>(() => dict.Add("k", "v2"));
        Assert.Equal("v1", dict["k"]);
        Assert.Single(dict);
    }

    [SkippableFact]
    public void Remove_MissingKey_ShouldReturnFalse_AndCountUnchanged()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:remove-missing:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict["k1"] = "v1";
        int before = dict.Count;
        bool removed = dict.Remove("not-found");

        Assert.False(removed);
        Assert.Equal(before, dict.Count);
    }

    [SkippableFact]
    public void Indexer_SetExistingKey_ShouldOverwrite_WithoutCountIncrease()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:indexer-overwrite:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict["k1"] = "v1";
        int count1 = dict.Count;
        dict["k1"] = "v2";

        Assert.Equal(count1, dict.Count);
        Assert.Equal("v2", dict["k1"]);
    }

    [SkippableFact]
    public void IReadOnlyDictionary_KeysValues_ShouldMatchCount()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:readonly:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);

        dict["a"] = "1";
        dict["b"] = "2";
        dict["c"] = "3";

        IReadOnlyDictionary<string, string> ro = dict;
        Assert.Equal(dict.Count, ro.Keys.Count());
        Assert.Equal(dict.Count, ro.Values.Count());
        Assert.True(ro.ContainsKey("a"));
    }
}
