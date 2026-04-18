# PLC.DistributedDictionary (NuGet)

.NET library: a **distributed dictionary** implementing `IDictionary<TKey, TValue>`, `IReadOnlyDictionary<TKey, TValue>`, and non-generic `IDictionary`. It combines **ZiggyCreatures.FusionCache** (JSON values), **RedLock.net** (per-key distributed locks), and **Redis** (SET index for keys, `Count`, `Keys`, `Values`).

- **NuGet package ID:** `PLC.DistributedDictionary` — [nuget.org/packages/PLC.DistributedDictionary](https://www.nuget.org/packages/PLC.DistributedDictionary) (after you publish)
- **Code namespace:** `PLC.Shared.DistributedConcurrentDictionary` (unchanged; `using` statements stay the same)
- **Package docs (install, quick start, API):** [src/DistributedConcurrentDictionary/README.md](src/DistributedConcurrentDictionary/README.md)

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Redis** for integration tests, the sample app, and E2E (e.g. `localhost:6379`)

## Repository layout

| Path | Purpose |
|------|---------|
| `src/DistributedConcurrentDictionary` | Library packaged to NuGet |
| `samples/DistributedConcurrentDictionary.Sample` | Spectre.Console demo (backplane, multi-process) |
| `tests/DistributedConcurrentDictionary.Tests` | xUnit tests (some scenarios skip without Redis) |
| `tests/e2e` | PowerShell multi-process checks against Redis |

Solution file: **DistributedConcurrentDictionary.sln**

## Build

```bash
dotnet build DistributedConcurrentDictionary.sln -c Release
```

## Tests

```bash
dotnet test tests/DistributedConcurrentDictionary.Tests/DistributedConcurrentDictionary.Tests.csproj -c Release
```

## Consume from your app

One command is enough. NuGet restores **ZiggyCreatures.FusionCache**, **RedLock.net**, and **StackExchange.Redis** automatically because they are **package dependencies** of this library (transitive restore).

```bash
dotnet add package PLC.DistributedDictionary
```

The **sample project** references extra packages (FusionCache Redis backplane, `Microsoft.Extensions.Caching.StackExchangeRedis`, Spectre, etc.). Those are only required for the demo, not for referencing the library.

## E2E (Redis + multiple processes)

```powershell
# Edit $repoRoot inside the script if your clone path differs
.\tests\e2e\run-backplane-e2e.ps1 -RedisConnection "localhost:6379"
```

Logs: `tests/e2e/results/<runId>/`.

## License

MIT (see `PackageLicenseExpression` in the library `.csproj`).
