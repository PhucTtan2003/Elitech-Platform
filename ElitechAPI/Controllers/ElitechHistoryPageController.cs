using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Elitech.Controllers
{
    [Authorize]
    public class ElitechHistoryPageController : Controller
    {
        public IActionResult Index()
        {
            ViewData["Title"] = "Elitech - Lịch sử thiết bị";

            var isAdmin = User.Claims.Any(c =>
                (c.Type == ClaimTypes.Role || c.Type == "role") &&
                string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));

            ViewBag.IsAdmin = isAdmin;

            return View("~/Views/ElitechHistoryPage/Index.cshtml");
        }
    }
}