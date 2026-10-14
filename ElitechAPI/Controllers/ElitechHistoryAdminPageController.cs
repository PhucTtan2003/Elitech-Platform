using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers
{
    [Authorize(Roles = "Admin,Staff")]
    [Route("ElitechHistoryAdminPage")]
    public class ElitechHistoryAdminPageController : Controller
    {
        [HttpGet("")]
        public IActionResult Index()
        {
            ViewData["Title"] = "Elitech - Lịch sử thiết bị Admin";
            ViewBag.Scope = "admin";
            ViewBag.IsAdmin = true;

            return View();
        }
    }
}