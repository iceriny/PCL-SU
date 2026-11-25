using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace PCL.Core.App.Configuration.Storage;

public class DynamicCacheConfigStorage : ConfigStorage
{
    private readonly Dictionary<object, ConfigStorage> _cache = [];
    private ConfigStorage? _nullContextCache;

    /// <summary>
    /// 存取仓库工厂。在没有匹配的上下文实例时将被调用，以创建新的上下文实例。
    /// </summary>
    public required Func<object?, ConfigStorage> StorageFactory { get; init; }

    protected override bool OnAccess<TKey, TValue>(StorageAction action, ref TKey key, [NotNullWhen(true)] ref TValue value, object? context)
    {
        lock (_cache)
        {
            ConfigStorage? storage;
            if (context is null) storage = _nullContextCache;
            else _cache.TryGetValue(context, out storage);
            if (storage is null)
            {
                try
                {
                    storage = StorageFactory(context);
                    if (context is null) _nullContextCache = storage;
                    else _cache[context] = storage;
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to invoke storage factory", ex);
                }
            }
            return storage.Access(action, ref key, ref value, context);
        }
    }

    protected override void OnStop()
    {
        lock (_cache)
        {
            foreach (var item in _cache.Values) item.Stop();
            _cache.Clear();
            _nullContextCache?.Stop();
            _nullContextCache = null;
        }
    }

    public bool InvalidateCache(object context)
    {
        lock (_cache)
        {
            if (!_cache.TryGetValue(context, out var center)) return false;
            center.Stop();
            _cache.Remove(context);
            return true;
        }
    }
}
