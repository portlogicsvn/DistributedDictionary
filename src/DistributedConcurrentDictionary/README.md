# PLC.Shared.DistributedConcurrentDictionary

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
dotnet add package PLC.Shared.DistributedConcurrentDictionary
dotnet add package ZiggyCreatures.FusionCache
dotnet add package RedLock.net
dotnet add package StackExchange.Redis
```

## Quick Start

```csharp
using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

ConnectionMultiplexer mux = ConnectionMultiplexer.Connect("localhost:6379");
FusionCache cache = new FusionCache(new FusionCacheOptions());
IDistributedLockFactory lockFactory = RedLockFactory.Create(new[] { new RedLockMultiplexer(mux) });

JsonFusionRedisDistributedDictionary<string, MyValue> dict = new JsonFusionRedisDistributedDictionary<string, MyValue>(
    cache,
    mux,
    lockFactory,
    new FusionRedisDictionaryOptions
    {
        KeyPrefix = "myapp:dict",
        DataCriticality = TosDataCriticality.Operational,
        FailureMode = FusionRedisFailureMode.ReadOnlyStale,
        RedisRetryCount = 2,
        RedisRetryDelay = TimeSpan.FromMilliseconds(100),
        CircuitBreakerFailureThreshold = 5,
        CircuitBreakerOpenDuration = TimeSpan.FromSeconds(10),
        OnTelemetry = evt =>
        {
            Console.WriteLine($"{evt.EventType} | {evt.Operation} | {evt.Message}");
        },
    });

dict["a"] = new MyValue();
bool found = dict.TryGetValue("a", out MyValue value);
FusionRedisDictionaryHealthSnapshot health = dict.GetHealthSnapshot();
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
