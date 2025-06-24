using Microsoft.Extensions.Caching.Distributed;

namespace SentryOperator.Services;

public class RemoteFileService
{
    private readonly HttpClient _httpClient;
    private readonly IDistributedCache _cache;

    public RemoteFileService(HttpClient httpClient, IDistributedCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
    }
    
    public async Task<string> GetAsync(string url, TimeSpan? cacheDuration = null)
    {
        // Check cache first
        var cachedContent = await _cache.GetStringAsync(url);
        if (cachedContent != null)
        {
            return cachedContent;
        }

        // If not in cache, fetch from remote URL
        var response = await _httpClient.GetAsync(url);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();

        // Store in cache
        await _cache.SetStringAsync(url, content, new DistributedCacheEntryOptions()
        {
            AbsoluteExpirationRelativeToNow = cacheDuration ?? TimeSpan.FromHours(24)
        });

        return content;
    }
    
    public string Get(string url, TimeSpan? cacheDuration = null)
    {
        // Check cache first
        var cachedContent = _cache.GetString(url);
        if (cachedContent != null)
        {
            return cachedContent;
        }

        // If not in cache, fetch from remote URL
        var response = _httpClient.GetAsync(url).GetAwaiter().GetResult();

        response.EnsureSuccessStatusCode();

        var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        // Store in cache
        _cache.SetString(url, content, new DistributedCacheEntryOptions()
        {
            AbsoluteExpirationRelativeToNow = cacheDuration ?? TimeSpan.FromHours(24)
        });

        return content;
    }
}