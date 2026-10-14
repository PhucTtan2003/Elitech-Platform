using System.Security.Claims;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[Authorize(Roles = "User")]
[Route("ElitechHistoryUser")]
public class ElitechHistoryUserPageController : Controller
{
    private readonly ElitechDeviceAssignmentService _assign;

    public ElitechHistoryUserPageController(ElitechDeviceAssignmentService assign)
    {
        _assign = assign;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Elitech - Lịch sử thiết bị của tôi";
        ViewBag.Scope = "user";

        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return RedirectToAction("Index", "Login");

        var devices = await _assign.GetByUserAsync(userId, ct);
        devices ??= new();

        ViewBag.Devices = devices;

        var guids = devices
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceGuid))
            .Select(x => x.DeviceGuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ViewBag.DeviceGuids = string.Join(",", guids);

        return View();
    }

    private string? GetUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? User.FindFirstValue("sub")
              ?? User.FindFirstValue("uid");

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}