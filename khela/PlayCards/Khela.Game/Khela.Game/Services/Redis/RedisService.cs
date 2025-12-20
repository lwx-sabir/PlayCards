using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using System.Text.Json; // Added for JSON serialization

namespace Khela.Game.Services.Redis
{
    public class RedisService : IRedisService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly IMemoryCache _cache;

        public RedisService(IConnectionMultiplexer redis, IMemoryCache memoryCache)
        {
            _redis = redis;
            _cache = memoryCache;
        }

        public IDatabase GetDatabase()
        {
            return _redis.GetDatabase();
        }

        public IMemoryCache GetMemoryCache()
        {
            return _cache;
        }

        public async Task<string> GetStringAsync(string key)
        {
            var db = _redis.GetDatabase();
            return await db.StringGetAsync(key);
        }

        public async Task SetStringAsync(string key, string value)
        {
            var db = _redis.GetDatabase();
            await db.StringSetAsync(key, value);
        } 

        public async Task<T> GetAsync<T>(string key)
        {
            var db = _redis.GetDatabase();
            RedisValue json = await db.StringGetAsync(key);
            if (json.IsNullOrEmpty)
            {
                return default; // Return default (null) if key doesn't exist
            }
            return JsonSerializer.Deserialize<T>(json);
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null)
        {
            var db = _redis.GetDatabase();
            var json = JsonSerializer.Serialize(value);
            await db.StringSetAsync(key, json, expiry);
        }

        public async Task DeleteAsync(string key)
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(key);
        }

        public async Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern)
        {
            var db = _redis.GetDatabase();
            // Assumes a single server endpoint, which is fine for this setup
            var server = _redis.GetServer(_redis.GetEndPoints().First());

            var keys = new List<string>();
            // Asynchronously stream keys matching the pattern
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                keys.Add(key.ToString());
            }
            return keys;
        }
    }
}