using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace SentryOperator.Extensions;

public static class DistributedCacheExtensions
{
    public static async Task<T> GetOrSetAsync<T>(this IDistributedCache cache, string key, Func<Task<T>> factory, DistributedCacheEntryOptions options)
    {
        var cached = await cache.GetStringAsync(key);
        if (cached != null)
        {
            return JsonSerializer.Deserialize<T>(cached)!;
        }

        var value = await factory();
        await cache.SetStringAsync(key, JsonSerializer.Serialize(value), options);
        return value;
    }
    
    public static async Task<T> GetOrSetAsync<T>(this IDistributedCache cache, string key, Func<Task<T>> factory, TimeSpan absoluteExpirationRelativeToNow)
    {
        return await cache.GetOrSetAsync(key, factory, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow
        });
    }
    
    public static async Task<T> GetOrSetAsync<T>(this IDistributedCache cache, string key, Func<Task<T>> factory, DateTimeOffset absoluteExpiration)
    {
        return await cache.GetOrSetAsync(key, factory, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = absoluteExpiration
        });
    }
    
    public static async Task<T> GetOrSetAsync<T>(this IDistributedCache cache, string key, Func<Task<T>> factory)
    {
        return await cache.GetOrSetAsync(key, factory, new DistributedCacheEntryOptions());
    }
    
    public static async Task<T?> GetAsync<T>(this IDistributedCache cache, string key)
    {
        var cached = await cache.GetStringAsync(key);
        return cached != null ? JsonSerializer.Deserialize<T>(cached) : default;
    }
    
    public static async Task SetAsync<T>(this IDistributedCache cache, string key, T value, DistributedCacheEntryOptions options)
    {
        await cache.SetStringAsync(key, JsonSerializer.Serialize(value), options);
    }
    
    public static async Task SetAsync<T>(this IDistributedCache cache, string key, T value, TimeSpan absoluteExpirationRelativeToNow)
    {
        await cache.SetAsync(key, value, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = absoluteExpirationRelativeToNow
        });
    }
    
    public static async Task SetAsync<T>(this IDistributedCache cache, string key, T value, DateTimeOffset absoluteExpiration)
    {
        await cache.SetAsync(key, value, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = absoluteExpiration
        });
    }
    
    public static async Task SetAsync<T>(this IDistributedCache cache, string key, T value)
    {
        await cache.SetAsync(key, value, new DistributedCacheEntryOptions());
    }
}