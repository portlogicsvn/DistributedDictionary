# PLC.DistributedDictionary (NuGet) / `PLC.Shared.DistributedConcurrentDictionary` (namespace)

Distributed dictionary for .NET, backed by:

- `ZiggyCreatures.FusionCache` for fast L1/L2 cache reads
- `RedLock.net` for cross-node write locking
- Redis `SET` index for `Count`, `Keys`, `Values`, and key existence
- Policy-driven resilience (retry + circuit breaker + degraded mode)
- Health snapshot and telemetry callback for microservice observability

Implemented interfaces:

- `IDictionary<TKey, TValue>`
- `IReadOnlyDictionary<TKey, TValue>`
- `IDictionary` (non-generic)

## Install

```bash
dotnet add package PLC.DistributedDictionary
```

Namespaces and types remain under **`PLC.Shared.DistributedConcurrentDictionary`** (e.g. `using PLC.Shared.DistributedConcurrentDictionary;`).

## Quick Start

Minimal setup — default `KeyPrefix` is `dcd`, Redis retry/circuit defaults apply, `FailureMode` is `FailFast`:

```csharp
using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

using ConnectionMultiplexer redis = ConnectionMultiplexer.Connect("localhost:6379");
FusionCache cache = new FusionCache(new FusionCacheOptions());
IDistributedLockFactory locks = RedLockFactory.Create(new[] { new RedLockMultiplexer(redis) });

JsonFusionRedisDistributedDictionary<string, MyValue> dict =
    new JsonFusionRedisDistributedDictionary<string, MyValue>(cache, redis, locks);

dict["a"] = new MyValue();
bool found = dict.TryGetValue("a", out MyValue? value);
FusionRedisDictionaryHealthSnapshot health = dict.GetHealthSnapshot();
```

Optional: isolate keys in Redis and relax failure handling (e.g. keep serving cache when Redis is down):

```csharp
var dict = new JsonFusionRedisDistributedDictionary<string, MyValue>(
    cache,
    redis,
    locks,
    new FusionRedisDictionaryOptions
    {
        KeyPrefix = "myapp:dict",
        FailureMode = FusionRedisFailureMode.ReadOnlyStale,
        OnTelemetry = static e => Console.WriteLine($"{e.EventType} | {e.Message}"),
    });
```

## Topics: register once, use by name

**Step 1 — `Program.cs` (or DI):** build shared `FusionCache`, Redis, RedLock once, plus a `DistributedTopicDictionaries` with a **base** `KeyPrefix` (e.g. `myapp`).

```csharp
using ConnectionMultiplexer redis = ConnectionMultiplexer.Connect("localhost:6379");
FusionCache cache = new FusionCache(new FusionCacheOptions());
IDistributedLockFactory locks = RedLockFactory.Create(new[] { new RedLockMultiplexer(redis) });

DistributedTopicDictionaries topics = new DistributedTopicDictionaries(
    cache,
    redis,
    locks,
    new FusionRedisDictionaryOptions { KeyPrefix = "myapp" });

// keep `topics` in DI / static / host service for the app lifetime
```

**Step 2 — anywhere:** resolve the same hub, then call `GetOrAdd<TKey,TValue>(topic)`. The first call for that topic + type pair creates the dictionary; later calls return the **same** instance. Redis keyspace is `myapp:{topic}:…` per topic.

```csharp
JsonFusionRedisDistributedDictionary<string, OrderDto> orders =
    topics.GetOrAdd<string, OrderDto>("orders");

orders[orderId] = dto;
```

Use a **distinct topic string** per logical stream (e.g. `"orders"`, `"invoices"`). The pair `(topic, TKey, TValue)` identifies the singleton; e.g. `GetOrAdd<string, OrderDto>("orders")` and `GetOrAdd<string, InvoiceDto>("orders")` are two different dictionaries.

### ASP.NET / `Microsoft.Extensions.DependencyInjection`

**Step 1 — `Program.cs`:** register the node once (`DictionaryDistributedNodeOptions` via lambda):

```csharp
using Microsoft.Extensions.DependencyInjection;
using PLC.Shared.DistributedConcurrentDictionary;

builder.Services.AddDistributedDictionaryNode(node =>
{
    node.RedisConnectionString = builder.Configuration["Redis"]!;
    node.ConfigureFusionCacheOptions = fc => { /* FusionCacheOptions */ };
    node.ConfigureDistributedDictionary = o =>
    {
        o.KeyPrefix = "myapp";
        o.FailureMode = FusionRedisFailureMode.ReadOnlyStale;
        o.OnTelemetry = static e => { /* ... */ };
    };
});
```

If `RedisConnectionString` is omitted, register `IConnectionMultiplexer` yourself before this call.

**Step 2 — any service:** inject `IDistributedDictionaryFactory` and open as many topics as needed:

```csharp
public sealed class OrderService(IDistributedDictionaryFactory dicts)
{
    public void Save(string id, OrderDto dto) => dicts.GetOrAdd<string, OrderDto>("orders")[id] = dto;
    public void SaveInvoice(string id, InvoiceDto inv) => dicts.GetOrAdd<string, InvoiceDto>("invoices")[id] = inv;
}
```

## Failure/Policy Modes

- `FailFast`: throw immediately when Redis/lock fails.
- `ReadOnlyStale`: keep serving cache reads when possible, while infra is unhealthy.
- `DataCriticality` (`Critical`, `Operational`, `Reference`) tunes default retry/circuit profile.

## Health + Telemetry

- `GetHealthSnapshot()` exposes degraded state, circuit status, failure counters, and last failure.
- `OnTelemetry` callback emits operation-level events:
  - read/write success
  - redis failure
  - lock failure
  - circuit opened/blocked
  - degraded reads

## Notes

- `TKey` and `TValue` are JSON-serialized.
- Read path is optimized for cache (`TryGet` from FusionCache).
- Writes (`Add`, indexer set, `Remove`, `Clear`) are lock-protected.
- For best consistency across nodes, use the same Redis and same `KeyPrefix`.
