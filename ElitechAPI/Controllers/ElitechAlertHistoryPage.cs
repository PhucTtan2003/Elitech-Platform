using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[Authorize]
public class ElitechAlertHistoryPageController : Controller
{
    public IActionResult Index([FromQuery] string? deviceGuid, [FromQuery] string? from, [FromQuery] string? to)
    {
        ViewData["Title"] = "Lịch sử cảnh báo";
        ViewBag.IsAdmin = User.IsInRole("Admin");
        ViewBag.DeviceGuid = deviceGuid ?? "";
        ViewBag.From = from ?? "";
        ViewBag.To = to ?? "";
        return View();
    }
}