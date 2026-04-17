using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RedLockNet;
using StackExchange.Redis;
using ZiggyCreatures.Caching.Fusion;

namespace PLC.Shared.DistributedConcurrentDictionary;

/// <summary>
/// Distributed <see cref="IDictionary{TKey,TValue}"/>: JSON values in FusionCache, key index in a Redis SET,
/// cross-node writes serialized with RedLock per key (global lock for <see cref="Clear"/>).
/// <see cref="TryGetValue"/> prefers FusionCache (L1+L2); <see cref="ContainsKey"/> uses the Redis SET in O(1).
/// </summary>
public sealed class JsonFusionRedisDistributedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>, IDictionary
    where TKey : notnull
{
    private readonly IFusionCache _cache;
    private readonly IDatabase _redis;
    private readonly IDistributedLockFactory _lockFactory;
    private readonly RedisKey _indexKey;
    private readonly string _lockScope;
    private readonly FusionCacheEntryOptions _entryOptions;
    private readonly JsonSerializerOptions _json;
    private readonly TimeSpan _lockExpiry;
    private readonly TimeSpan _lockWait;
    private readonly TimeSpan _lockRetry;
    private readonly FusionRedisFailureMode _failureMode;
    private readonly int _redisRetryCount;
    private readonly TimeSpan _redisRetryDelay;
    private readonly int _circuitFailureThreshold;
    private readonly TimeSpan _circuitOpenDuration;
    private readonly Action<FusionRedisTelemetryEvent>? _onTelemetry;
    private long _redisFailureCount;
    private long _lockFailureCount;
    private int _consecutiveRedisFailures;
    private DateTimeOffset? _circuitOpenUntilUtc;
    private DateTimeOffset? _lastFailureUtc;
    private string? _lastFailureMessage;

    public JsonFusionRedisDistributedDictionary(
        IFusionCache cache,
        IConnectionMultiplexer redisConnection,
        IDistributedLockFactory lockFactory,
        FusionRedisDictionaryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(redisConnection);
        ArgumentNullException.ThrowIfNull(lockFactory);

        FusionRedisDictionaryOptions o = options ?? new FusionRedisDictionaryOptions();
        if (string.IsNullOrWhiteSpace(o.KeyPrefix))
        {
            throw new ArgumentException("KeyPrefix is required.", nameof(options));
        }

        _cache = cache;
        _redis = redisConnection.GetDatabase();
        _lockFactory = lockFactory;
        string prefix = o.KeyPrefix.TrimEnd(':');
        _indexKey = prefix + ":idx";
        _lockScope = prefix + ":lck";
        _entryOptions = o.DefaultEntryOptions ?? new FusionCacheEntryOptions();
        _json = o.JsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.General);
        _lockExpiry = o.LockExpiry;
        _lockWait = o.LockWait;
        _lockRetry = o.LockRetry;
        _failureMode = o.FailureMode;

        TosDataCriticality criticality = o.DataCriticality;
        if (criticality == TosDataCriticality.Critical)
        {
            _redisRetryCount = o.RedisRetryCount > 0 ? o.RedisRetryCount : 3;
            _redisRetryDelay = o.RedisRetryDelay > TimeSpan.Zero ? o.RedisRetryDelay : TimeSpan.FromMilliseconds(80);
            _circuitFailureThreshold = o.CircuitBreakerFailureThreshold > 0 ? o.CircuitBreakerFailureThreshold : 4;
            _circuitOpenDuration = o.CircuitBreakerOpenDuration > TimeSpan.Zero ? o.CircuitBreakerOpenDuration : TimeSpan.FromSeconds(6);
        }
        else if (criticality == TosDataCriticality.Reference)
        {
            _redisRetryCount = o.RedisRetryCount > 0 ? o.RedisRetryCount : 1;
            _redisRetryDelay = o.RedisRetryDelay > TimeSpan.Zero ? o.RedisRetryDelay : TimeSpan.FromMilliseconds(200);
            _circuitFailureThreshold = o.CircuitBreakerFailureThreshold > 0 ? o.CircuitBreakerFailureThreshold : 8;
            _circuitOpenDuration = o.CircuitBreakerOpenDuration > TimeSpan.Zero ? o.CircuitBreakerOpenDuration : TimeSpan.FromSeconds(20);
        }
        else
        {
            _redisRetryCount = o.RedisRetryCount > 0 ? o.RedisRetryCount : 2;
            _redisRetryDelay = o.RedisRetryDelay > TimeSpan.Zero ? o.RedisRetryDelay : TimeSpan.FromMilliseconds(100);
            _circuitFailureThreshold = o.CircuitBreakerFailureThreshold > 0 ? o.CircuitBreakerFailureThreshold : 5;
            _circuitOpenDuration = o.CircuitBreakerOpenDuration > TimeSpan.Zero ? o.CircuitBreakerOpenDuration : TimeSpan.FromSeconds(10);
        }

        _onTelemetry = o.OnTelemetry;
    }

    public TValue this[TKey key]
    {
        get
        {
            if (!TryGetValue(key, out TValue? value))
            {
                throw new KeyNotFoundException();
            }

            return value;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            string ek = EncodeKey(key);
            string ck = CacheKey(ek);
            using (IRedLock redLock = AcquireKeyLock(ek))
            {
                _cache.Set(ck, SerializeValue(value), _entryOptions);
                ExecuteRedis("IndexerSet.SetAdd", ek, (self) => self._redis.SetAdd(self._indexKey, ek));
                EmitTelemetry(FusionRedisEventType.WriteSuccess, "IndexerSet", ek, TimeSpan.Zero, null, "Indexer set completed.");
            }
        }
    }

    public ICollection<TKey> Keys => new KeyCollection(this);

    public ICollection<TValue> Values => new ValueCollection(this);

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public int Count
    {
        get
        {
            long n = ExecuteRedis("Count", null, static (self) => self._redis.SetLength(self._indexKey));
            if (n > int.MaxValue)
            {
                throw new OverflowException("Set size exceeds Int32.MaxValue.");
            }

            return (int)n;
        }
    }

    public bool IsReadOnly => false;

    public FusionRedisDictionaryHealthSnapshot GetHealthSnapshot()
    {
        return new FusionRedisDictionaryHealthSnapshot
        {
            IsDegraded = IsCircuitOpen(),
            IsCircuitOpen = IsCircuitOpen(),
            CircuitOpenUntilUtc = _circuitOpenUntilUtc,
            RedisFailureCount = Interlocked.Read(ref _redisFailureCount),
            LockFailureCount = Interlocked.Read(ref _lockFailureCount),
            LastFailureUtc = _lastFailureUtc,
            LastFailureMessage = _lastFailureMessage,
        };
    }

    bool IDictionary.IsReadOnly => false;

    bool IDictionary.IsFixedSize => false;

    ICollection IDictionary.Keys => new ArrayList(Keys.ToArray());

    ICollection IDictionary.Values => new ArrayList(Values.ToArray());

    object ICollection.SyncRoot => this;

    bool ICollection.IsSynchronized => false;

    public void Add(TKey key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        string ek = EncodeKey(key);
        string ck = CacheKey(ek);
        using (IRedLock redLock = AcquireKeyLock(ek))
        {
            if (ExecuteRedis("Add.SetContains", ek, (self) => self._redis.SetContains(self._indexKey, ek)))
            {
                throw new ArgumentException("An item with the same key already exists.", nameof(key));
            }

            _cache.Set(ck, SerializeValue(value), _entryOptions);
            if (!ExecuteRedis("Add.SetAdd", ek, (self) => self._redis.SetAdd(self._indexKey, ek)))
            {
                _cache.Remove(ck);
                throw new ArgumentException("An item with the same key already exists.", nameof(key));
            }
            EmitTelemetry(FusionRedisEventType.WriteSuccess, "Add", ek, TimeSpan.Zero, null, "Add completed.");
        }
    }

    public void Add(KeyValuePair<TKey, TValue> item)
    {
        Add(item.Key, item.Value);
    }

    void IDictionary.Add(object key, object? value)
    {
        TKey typedKey = CastObjectKey(key);
        TValue typedValue = CastObjectValue(value);
        Add(typedKey, typedValue);
    }

    public void Clear()
    {
        string resource = _lockScope + ":clear";
        using (IRedLock redLock = _lockFactory.CreateLock(resource, _lockExpiry, _lockWait, _lockRetry))
        {
            if (!redLock.IsAcquired)
            {
                throw new TimeoutException("Could not acquire global clear lock.");
            }

            RedisValue[] members = ExecuteRedis("Clear.SetMembers", null, static (self) => self._redis.SetMembers(self._indexKey));
            foreach (RedisValue rv in members)
            {
                string ek = rv.ToString();
                _cache.Remove(CacheKey(ek));
            }

            ExecuteRedis("Clear.KeyDelete", null, static (self) => self._redis.KeyDelete(self._indexKey));
            EmitTelemetry(FusionRedisEventType.RemoveSuccess, "Clear", null, TimeSpan.Zero, null, "Clear completed.");
        }
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        if (!TryGetValue(item.Key, out TValue? v))
        {
            return false;
        }

        return EqualityComparer<TValue>.Default.Equals(v, item.Value);
    }

    bool IDictionary.Contains(object key)
    {
        if (!TryCastObjectKey(key, out TKey? typedKey))
        {
            return false;
        }

        return ContainsKey(typedKey);
    }

    public bool ContainsKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string ek = EncodeKey(key);
        if (_cache.TryGet<string>(CacheKey(ek), null, default).HasValue)
        {
            return true;
        }

        return ExecuteRedis("ContainsKey.SetContains", ek, (self) => self._redis.SetContains(self._indexKey, ek));
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        }

        int c = Count;
        if (array.Length - arrayIndex < c)
        {
            throw new ArgumentException("Destination array is not long enough.");
        }

        foreach (KeyValuePair<TKey, TValue> pair in this)
        {
            array[arrayIndex++] = pair;
        }
    }

    void ICollection.CopyTo(Array array, int index)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Rank != 1)
        {
            throw new ArgumentException("Array must be one-dimensional.", nameof(array));
        }

        if (array.GetLowerBound(0) != 0)
        {
            throw new ArgumentException("Array must have zero-based indexing.", nameof(array));
        }

        if (index < 0 || index > array.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (array.Length - index < Count)
        {
            throw new ArgumentException("Destination array is not long enough.", nameof(array));
        }

        if (array is DictionaryEntry[] entryArray)
        {
            foreach (KeyValuePair<TKey, TValue> pair in this)
            {
                entryArray[index++] = new DictionaryEntry(pair.Key!, pair.Value);
            }

            return;
        }

        if (array is object[] objectArray)
        {
            foreach (KeyValuePair<TKey, TValue> pair in this)
            {
                objectArray[index++] = new DictionaryEntry(pair.Key!, pair.Value);
            }

            return;
        }

        throw new ArgumentException("Unsupported array type.", nameof(array));
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        RedisValue[] members = ExecuteRedis("GetEnumerator.SetMembers", null, static (self) => self._redis.SetMembers(self._indexKey));
        foreach (RedisValue rv in members)
        {
            string ek = rv.ToString();
            TKey dk = DecodeKey(ek);
            MaybeValue<string> m = _cache.TryGet<string>(CacheKey(ek), null, default);
            if (m.HasValue)
            {
                yield return new KeyValuePair<TKey, TValue>(dk, DeserializeValue(m.Value));
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    IDictionaryEnumerator IDictionary.GetEnumerator()
    {
        return new NonGenericEnumeratorAdapter(GetEnumerator());
    }

    public bool Remove(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string ek = EncodeKey(key);
        string ck = CacheKey(ek);
        using (IRedLock redLock = AcquireKeyLock(ek))
        {
            _cache.Remove(ck);
            bool removed = ExecuteRedis("Remove.SetRemove", ek, (self) => self._redis.SetRemove(self._indexKey, ek));
            EmitTelemetry(FusionRedisEventType.RemoveSuccess, "Remove", ek, TimeSpan.Zero, null, removed ? "Removed." : "Key not found.");
            return removed;
        }
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        if (!Contains(item))
        {
            return false;
        }

        return Remove(item.Key);
    }

    void IDictionary.Remove(object key)
    {
        if (!TryCastObjectKey(key, out TKey? typedKey))
        {
            return;
        }

        Remove(typedKey);
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        string ek = EncodeKey(key);
        string ck = CacheKey(ek);
        MaybeValue<string> m = _cache.TryGet<string>(ck, null, default);
        if (m.HasValue)
        {
            EmitTelemetry(FusionRedisEventType.ReadSuccess, "TryGetValue", ek, TimeSpan.Zero, null, "Cache hit.");
            value = DeserializeValue(m.Value);
            return true;
        }

        EmitTelemetry(FusionRedisEventType.ReadMiss, "TryGetValue", ek, TimeSpan.Zero, null, "Cache miss.");
        value = default!;
        return false;
    }

    object? IDictionary.this[object key]
    {
        get
        {
            if (!TryCastObjectKey(key, out TKey? typedKey))
            {
                return null;
            }

            if (!TryGetValue(typedKey, out TValue? value))
            {
                return null;
            }

            return value;
        }
        set
        {
            TKey typedKey = CastObjectKey(key);
            TValue typedValue = CastObjectValue(value);
            this[typedKey] = typedValue;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string CacheKey(string encodedKey)
    {
        return _lockScope + ":v:" + encodedKey;
    }

    private string EncodeKey(TKey key)
    {
        string jsonKey = JsonSerializer.Serialize(key, _json);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonKey));
    }

    private TKey DecodeKey(string encodedKey)
    {
        byte[] bytes = Convert.FromBase64String(encodedKey);
        string jsonKey = Encoding.UTF8.GetString(bytes);
        TKey? k = JsonSerializer.Deserialize<TKey>(jsonKey, _json);
        if (k is null)
        {
            throw new InvalidOperationException("Decoded key is null.");
        }

        return k;
    }

    private string SerializeValue(TValue value)
    {
        return JsonSerializer.Serialize(value, _json);
    }

    private TValue DeserializeValue(string json)
    {
        TValue? v = JsonSerializer.Deserialize<TValue>(json, _json);
        if (v is null && default(TValue) is not null)
        {
            throw new JsonException("Value deserialized to null for non-nullable TValue.");
        }

        return v!;
    }

    private IRedLock AcquireKeyLock(string encodedKey)
    {
        if (IsCircuitOpen())
        {
            EmitTelemetry(FusionRedisEventType.CircuitBlocked, "AcquireKeyLock", encodedKey, TimeSpan.Zero, null, "Circuit is open; lock blocked.");
            throw new InvalidOperationException("Redis circuit is open. Write operations are temporarily blocked.");
        }

        string resource = _lockScope + ":k:" + encodedKey;
        Stopwatch sw = Stopwatch.StartNew();
        IRedLock redLock = _lockFactory.CreateLock(resource, _lockExpiry, _lockWait, _lockRetry);
        if (!redLock.IsAcquired)
        {
            redLock.Dispose();
            Interlocked.Increment(ref _lockFailureCount);
            _lastFailureUtc = DateTimeOffset.UtcNow;
            _lastFailureMessage = "Could not acquire key lock: " + resource;
            EmitTelemetry(FusionRedisEventType.LockAcquireFailure, "AcquireKeyLock", encodedKey, sw.Elapsed, null, _lastFailureMessage);
            throw new TimeoutException("Could not acquire key lock: " + resource);
        }

        EmitTelemetry(FusionRedisEventType.WriteSuccess, "AcquireKeyLock", encodedKey, sw.Elapsed, null, "Lock acquired.");
        return redLock;
    }

    private TResult ExecuteRedis<TResult>(string operation, string? encodedKey, Func<JsonFusionRedisDistributedDictionary<TKey, TValue>, TResult> action)
    {
        if (IsCircuitOpen())
        {
            EmitTelemetry(FusionRedisEventType.CircuitBlocked, operation, encodedKey, TimeSpan.Zero, null, "Circuit is open.");
            throw new InvalidOperationException("Redis circuit is open.");
        }

        Exception? lastException = null;
        for (int attempt = 0; attempt <= _redisRetryCount; attempt++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                TResult result = action(this);
                ResetRedisFailureStreak();
                return result;
            }
            catch (Exception ex) when (IsRedisRelatedException(ex))
            {
                lastException = ex;
                HandleRedisFailure(operation, encodedKey, sw.Elapsed, ex);
                if (attempt == _redisRetryCount)
                {
                    break;
                }

                if (_redisRetryDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(_redisRetryDelay);
                }
            }
        }

        if (_failureMode == FusionRedisFailureMode.ReadOnlyStale)
        {
            EmitTelemetry(FusionRedisEventType.DegradedRead, operation, encodedKey, TimeSpan.Zero, lastException, "Redis failed; degraded mode active.");
        }

        throw new InvalidOperationException("Redis operation failed: " + operation, lastException);
    }

    private static bool IsRedisRelatedException(Exception ex)
    {
        return ex is RedisException || ex is TimeoutException || ex is ObjectDisposedException;
    }

    private void HandleRedisFailure(string operation, string? encodedKey, TimeSpan duration, Exception exception)
    {
        Interlocked.Increment(ref _redisFailureCount);
        int streak = Interlocked.Increment(ref _consecutiveRedisFailures);
        _lastFailureUtc = DateTimeOffset.UtcNow;
        _lastFailureMessage = exception.Message;
        EmitTelemetry(FusionRedisEventType.RedisFailure, operation, encodedKey, duration, exception, "Redis failure.");

        if (streak >= _circuitFailureThreshold)
        {
            _circuitOpenUntilUtc = DateTimeOffset.UtcNow.Add(_circuitOpenDuration);
            EmitTelemetry(FusionRedisEventType.CircuitOpened, operation, encodedKey, duration, exception, "Circuit opened.");
        }
    }

    private void ResetRedisFailureStreak()
    {
        if (Interlocked.Exchange(ref _consecutiveRedisFailures, 0) >= _circuitFailureThreshold)
        {
            _circuitOpenUntilUtc = null;
        }
    }

    private bool IsCircuitOpen()
    {
        DateTimeOffset? openUntil = _circuitOpenUntilUtc;
        if (openUntil is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow < openUntil.Value)
        {
            return true;
        }

        _circuitOpenUntilUtc = null;
        Interlocked.Exchange(ref _consecutiveRedisFailures, 0);
        return false;
    }

    private void EmitTelemetry(FusionRedisEventType eventType, string operation, string? encodedKey, TimeSpan duration, Exception? exception, string message)
    {
        Action<FusionRedisTelemetryEvent>? callback = _onTelemetry;
        if (callback is null)
        {
            return;
        }

        callback(
            new FusionRedisTelemetryEvent
            {
                EventType = eventType,
                Operation = operation,
                Key = encodedKey,
                Duration = duration,
                Exception = exception,
                Message = message,
            });
    }

    private static TKey CastObjectKey(object key)
    {
        if (key is TKey typedKey)
        {
            return typedKey;
        }

        if (key is null)
        {
            throw new ArgumentNullException(nameof(key));
        }

        throw new ArgumentException($"Key must be of type {typeof(TKey).FullName}.", nameof(key));
    }

    private static bool TryCastObjectKey(object key, [MaybeNullWhen(false)] out TKey typedKey)
    {
        if (key is TKey k)
        {
            typedKey = k;
            return true;
        }

        typedKey = default;
        return false;
    }

    private static TValue CastObjectValue(object? value)
    {
        if (value is TValue typedValue)
        {
            return typedValue;
        }

        if (value is null && default(TValue) is null)
        {
            return default!;
        }

        throw new ArgumentException($"Value must be of type {typeof(TValue).FullName}.", nameof(value));
    }

    private sealed class KeyCollection : ICollection<TKey>
    {
        private readonly JsonFusionRedisDistributedDictionary<TKey, TValue> _owner;

        public KeyCollection(JsonFusionRedisDistributedDictionary<TKey, TValue> owner)
        {
            _owner = owner;
        }

        public int Count => _owner.Count;

        public bool IsReadOnly => true;

        public void Add(TKey item)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public bool Contains(TKey item)
        {
            return _owner.ContainsKey(item);
        }

        public void CopyTo(TKey[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (arrayIndex < 0 || arrayIndex > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            int c = Count;
            if (array.Length - arrayIndex < c)
            {
                throw new ArgumentException("Destination array is not long enough.");
            }

            foreach (TKey k in this)
            {
                array[arrayIndex++] = k;
            }
        }

        public IEnumerator<TKey> GetEnumerator()
        {
            RedisValue[] members = _owner.ExecuteRedis("KeyCollection.SetMembers", null, static (self) => self._redis.SetMembers(self._indexKey));
            foreach (RedisValue rv in members)
            {
                yield return _owner.DecodeKey(rv.ToString());
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool Remove(TKey item)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ValueCollection : ICollection<TValue>
    {
        private readonly JsonFusionRedisDistributedDictionary<TKey, TValue> _owner;

        public ValueCollection(JsonFusionRedisDistributedDictionary<TKey, TValue> owner)
        {
            _owner = owner;
        }

        public int Count => _owner.Count;

        public bool IsReadOnly => true;

        public void Add(TValue item)
        {
            throw new NotSupportedException();
        }

        public void Clear()
        {
            throw new NotSupportedException();
        }

        public bool Contains(TValue item)
        {
            EqualityComparer<TValue> cmp = EqualityComparer<TValue>.Default;
            foreach (TValue v in this)
            {
                if (cmp.Equals(v, item))
                {
                    return true;
                }
            }

            return false;
        }

        public void CopyTo(TValue[] array, int arrayIndex)
        {
            ArgumentNullException.ThrowIfNull(array);
            if (arrayIndex < 0 || arrayIndex > array.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            }

            int c = Count;
            if (array.Length - arrayIndex < c)
            {
                throw new ArgumentException("Destination array is not long enough.");
            }

            foreach (TValue v in this)
            {
                array[arrayIndex++] = v;
            }
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            foreach (KeyValuePair<TKey, TValue> pair in _owner)
            {
                yield return pair.Value;
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool Remove(TValue item)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NonGenericEnumeratorAdapter : IDictionaryEnumerator
    {
        private readonly IEnumerator<KeyValuePair<TKey, TValue>> _inner;

        public NonGenericEnumeratorAdapter(IEnumerator<KeyValuePair<TKey, TValue>> inner)
        {
            _inner = inner;
        }

        public DictionaryEntry Entry => new DictionaryEntry(_inner.Current.Key!, _inner.Current.Value);

        public object Key => _inner.Current.Key!;

        public object? Value => _inner.Current.Value;

        public object Current => Entry;

        public bool MoveNext()
        {
            return _inner.MoveNext();
        }

        public void Reset()
        {
            _inner.Reset();
        }
    }
}
