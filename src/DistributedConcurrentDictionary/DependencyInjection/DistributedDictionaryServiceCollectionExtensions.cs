using Microsoft.Extensions.DependencyInjection.Extensions;
using PLC.Shared.DistributedConcurrentDictionary;
using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers a distributed dictionary node: Redis, FusionCache, RedLock, and <see cref="IDistributedDictionaryFactory"/>.</summary>
public static class DistributedDictionaryServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure and <see cref="IDistributedDictionaryFactory"/> (backed by <see cref="DistributedTopicDictionaries"/>).
    /// Services can inject the factory and call <see cref="IDistributedDictionaryFactory.GetOrAdd{TKey,TValue}"/> for any number of topics.
    /// </summary>
    public static IServiceCollection AddDistributedDictionaryNode(
        this IServiceCollection services,
        Action<DictionaryDistributedNodeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        DictionaryDistributedNodeOptions nodeOptions = new DictionaryDistributedNodeOptions();
        configure(nodeOptions);

        if (!string.IsNullOrWhiteSpace(nodeOptions.RedisConnectionString))
        {
            services.TryAddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(nodeOptions.RedisConnectionString!));
        }

        services.TryAddSingleton<IFusionCache>(sp =>
        {
            FusionCacheOptions fusionOpts = new FusionCacheOptions();
            nodeOptions.ConfigureFusionCacheOptions?.Invoke(fusionOpts);
            return new FusionCache(fusionOpts);
        });

        services.TryAddSingleton<IDistributedLockFactory>(sp =>
        {
            IConnectionMultiplexer mux = sp.GetRequiredService<IConnectionMultiplexer>();
            return RedLockFactory.Create(new[] { new RedLockMultiplexer(mux) });
        });

        services.TryAddSingleton<DistributedTopicDictionaries>(sp =>
        {
            IFusionCache cache = sp.GetRequiredService<IFusionCache>();
            IConnectionMultiplexer mux = sp.GetRequiredService<IConnectionMultiplexer>();
            IDistributedLockFactory locks = sp.GetRequiredService<IDistributedLockFactory>();
            FusionRedisDictionaryOptions dictOpts = new FusionRedisDictionaryOptions();
            nodeOptions.ConfigureDistributedDictionary?.Invoke(dictOpts);
            if (string.IsNullOrWhiteSpace(dictOpts.KeyPrefix))
            {
                dictOpts.KeyPrefix = "dcd";
            }

            return new DistributedTopicDictionaries(cache, mux, locks, dictOpts);
        });

        services.TryAddSingleton<IDistributedDictionaryFactory>(sp =>
            sp.GetRequiredService<DistributedTopicDictionaries>());

        return services;
    }
}
