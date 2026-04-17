using System.Collections;
using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary.Tests;

public sealed class InteropAndSerializationTests
{
    [SkippableFact]
    public void NonGeneric_IDictionary_ShouldSupportBasicOperations()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:nongeneric:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);
        IDictionary ng = dict;

        ng.Add("k1", "v1");
        Assert.True(ng.Contains("k1"));
        Assert.Equal("v1", ng["k1"]);

        ng["k1"] = "v2";
        Assert.Equal("v2", ng["k1"]);

        IDictionaryEnumerator en = ng.GetEnumerator();
        Assert.True(en.MoveNext());
        Assert.IsType<DictionaryEntry>(en.Current);

        DictionaryEntry[] arr = new DictionaryEntry[dict.Count];
        ng.CopyTo(arr, 0);
        Assert.Contains(arr, x => (string)x.Key == "k1");

        ng.Remove("k1");
        Assert.False(ng.Contains("k1"));
    }

    [SkippableFact]
    public void CopyTo_InvalidArguments_ShouldThrow()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:copyto:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<string, string> dict = TestSupport.CreateStringDictionary(cache, mux, lockFactory, prefix);
        dict["k1"] = "v1";
        dict["k2"] = "v2";

        Assert.Throws<ArgumentNullException>(() => dict.CopyTo(null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => dict.CopyTo(new KeyValuePair<string, string>[2], -1));
        Assert.Throws<ArgumentException>(() => dict.CopyTo(new KeyValuePair<string, string>[1], 0));
    }

    [SkippableFact]
    public void Json_RoundTrip_WithComplexKey_AndNullValue_ShouldWork()
    {
        using ConnectionMultiplexer mux = TestSupport.ConnectOrSkip();
        using FusionCache cache = new FusionCache(new FusionCacheOptions());
        IDistributedLockFactory lockFactory = TestSupport.CreateLockFactory(mux);
        string prefix = "test:json-roundtrip:" + Guid.NewGuid().ToString("N");
        JsonFusionRedisDistributedDictionary<LaneKey, LaneInfo?> dict = new JsonFusionRedisDistributedDictionary<LaneKey, LaneInfo?>(
            cache,
            mux,
            lockFactory,
            new FusionRedisDictionaryOptions
            {
                KeyPrefix = prefix,
            });

        LaneKey key = new LaneKey("service-gate", "IN1");
        dict[key] = null;
        Assert.True(dict.TryGetValue(key, out LaneInfo? nullValue));
        Assert.Null(nullValue);

        dict[key] = new LaneInfo { LaneNo = "IN1", Direction = "IN" };
        Assert.Equal("IN1", dict[key]!.LaneNo);
    }
}
