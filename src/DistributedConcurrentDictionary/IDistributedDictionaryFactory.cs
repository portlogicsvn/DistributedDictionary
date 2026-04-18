namespace PLC.Shared.DistributedConcurrentDictionary;

/// <summary>
/// Resolves per-topic <see cref="JsonFusionRedisDistributedDictionary{TKey,TValue}"/> instances (singleton per topic and type pair).
/// Register once via extension method <c>AddDistributedDictionaryNode</c> (namespace <c>Microsoft.Extensions.DependencyInjection</c>).
/// </summary>
public interface IDistributedDictionaryFactory
{
    /// <inheritdoc cref="DistributedTopicDictionaries.GetOrAdd{TKey,TValue}"/>
    JsonFusionRedisDistributedDictionary<TKey, TValue> GetOrAdd<TKey, TValue>(string topic)
        where TKey : notnull;
}
