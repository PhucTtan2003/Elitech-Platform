using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Staff
{
    [Authorize(Roles = "Staff,Admin")]
    public class StaffController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
