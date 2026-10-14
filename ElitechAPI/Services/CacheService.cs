using Microsoft.Extensions.Caching.Memory;

namespace Elitech.Services
{
    public class CacheService
    {
        private readonly IMemoryCache _cache;

        public CacheService(IMemoryCache cache)
        {
            _cache = cache;
        }

       
        public void ClearAll()
        {
            if (_cache is MemoryCache memCache)
            {
                memCache.Compact(1.0);
            }
        }

        
        public void Remove(string key)
        {
            _cache.Remove(key);
        }
    }
}