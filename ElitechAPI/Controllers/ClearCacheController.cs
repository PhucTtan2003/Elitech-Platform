using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Elitech.Services;

namespace Elitech.Controllers
{
    [Route("clear")]
    [Authorize(Roles = "Admin")] 
    public class ClearCacheController : Controller
    {
        private readonly CacheService _cacheService;
        private readonly ILogger<ClearCacheController> _logger;

        public ClearCacheController(CacheService cacheService, ILogger<ClearCacheController> logger)
        {
            _cacheService = cacheService;
            _logger = logger;
        }

        [HttpGet("")]
        public IActionResult Clear()
        {
            
            _cacheService.ClearAll();

            
            _logger.LogWarning("⚠️ Cache cleared by {user} at {time}",
                User.Identity?.Name ?? "unknown",
                DateTime.Now);

            
            return Content(@"
                <script>
                    console.log('Cache cleared');
                   
                    sessionStorage.clear();
                    location.href = '/';
                </script>
            ", "text/html");
        }
    }
}