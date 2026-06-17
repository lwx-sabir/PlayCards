using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Khela.Game.Services.Redis
{
    public interface IRedisService
    {
        IDatabase GetDatabase();
        IMemoryCache GetMemoryCache();
        Task<string> GetStringAsync(string key);
        Task SetStringAsync(string key, string value);

        /// <summary>
        /// Gets an object from Redis by key, deserializing it from JSON.
        /// </summary>
        Task<T> GetAsync<T>(string key);

        /// <summary>
        /// Sets an object in Redis, serializing it to JSON.
        /// </summary>
        Task SetAsync<T>(string key, T value, TimeSpan? expiry = null);

        /// <summary>
        /// Deletes a key from Redis.
        /// </summary>
        Task DeleteAsync(string key);

        /// <summary>
        /// Gets all keys matching a specific pattern (e.g., "snake:*").
        /// </summary>
        Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern);
    }
}