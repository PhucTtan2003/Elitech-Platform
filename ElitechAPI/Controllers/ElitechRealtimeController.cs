using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Elitech.Services;
using System.Security.Claims;

namespace Elitech.Controllers;

[Authorize]
public class ElitechRealtimeController : Controller
{
    private readonly ElitechApiClient _api;
    private readonly ElitechDeviceAssignmentService _assign;

    public ElitechRealtimeController(
        ElitechApiClient api,
        ElitechDeviceAssignmentService assign)
    {
        _api = api;
        _assign = assign;
    }

    // UI:
    // /ElitechRealtime
    // Admin => auto lấy all device GUID từ Elitech Cloud
    // User  => auto lấy device đã gán trong Mongo
    [HttpGet]
    public async Task<IActionResult> Index(string? deviceGuids = null, CancellationToken ct = default)
    {
        ViewData["Title"] = "Elitech Realtime Monitor";

        var isAdmin = IsAdmin();
        ViewBag.IsAdmin = isAdmin;

        string finalGuids = "";

        // Nếu URL có truyền deviceGuids thì ưu tiên dùng
        if (!string.IsNullOrWhiteSpace(deviceGuids))
        {
            finalGuids = deviceGuids.Trim();
        }
        else if (isAdmin)
        {
            // Admin: lấy toàn bộ thiết bị từ Elitech Cloud
            var resp = await _api.GetDeviceGuidsAsync(ct);

            if (resp?.Code == 0 && resp.Data != null)
            {
                finalGuids = string.Join(",",
                    resp.Data
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                );
            }

            ViewBag.Devices = new List<object>();
        }
        else
        {
            // User: chỉ lấy thiết bị đã gán
            var userId = GetUserId();

            if (string.IsNullOrWhiteSpace(userId))
                return RedirectToAction("Index", "Login");

            var devices = await _assign.GetByUserAsync(userId, ct);
            devices ??= new();

            finalGuids = string.Join(",",
                devices
                    .Where(x => !string.IsNullOrWhiteSpace(x.DeviceGuid))
                    .Select(x => x.DeviceGuid.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
            );

            ViewBag.Devices = devices;
        }

        ViewBag.DeviceGuids = finalGuids ?? "";
        ViewBag.RoleText = isAdmin ? "Admin" : "User";

        return View("~/Views/ElitechRealtime/Index.cshtml");
    }

    // API:
    // GET /api/elitech/realtime
    // Admin => tự lấy all devices
    // User  => tự lấy assigned devices
    //
    // GET /api/elitech/realtime?deviceGuids=GUID1,GUID2
    // Admin => cho xem
    // User  => kiểm tra thiết bị có được gán không
    [HttpGet("/api/elitech/realtime")]
    public async Task<IActionResult> GetRealtime([FromQuery] string? deviceGuids, CancellationToken ct)
    {
        var isAdmin = IsAdmin();

        var list = (deviceGuids ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Nếu không truyền deviceGuids thì tự lấy theo quyền
        if (list.Count == 0)
        {
            if (isAdmin)
            {
                var resp = await _api.GetDeviceGuidsAsync(ct);

                if (resp == null)
                    return StatusCode(502, new { code = -1, message = "Empty Elitech response" });

                if (resp.Code != 0)
                {
                    return StatusCode(502, new
                    {
                        code = resp.Code,
                        message = resp.Msg ?? resp.Message ?? resp.Error ?? "Elitech upstream error",
                        error = resp.Error
                    });
                }

                list = (resp.Data ?? new List<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                var userId = GetUserId();

                if (string.IsNullOrWhiteSpace(userId))
                    return Unauthorized(new { code = 401, message = "Missing userId claim" });

                var assigned = await _assign.GetDeviceGuidsOfUserAsync(userId, ct);
                list = assigned.ToList();

                if (list.Count == 0)
                {
                    return Ok(new
                    {
                        code = 0,
                        data = Array.Empty<object>(),
                        message = "No assigned devices"
                    });
                }
            }
        }

        if (list.Count == 0)
        {
            return Ok(new
            {
                code = 0,
                data = Array.Empty<object>(),
                message = "No devices"
            });
        }

        // User không được override sang thiết bị không được gán
        if (!isAdmin)
        {
            var userId = GetUserId();

            if (string.IsNullOrWhiteSpace(userId))
                return Unauthorized(new { code = 401, message = "Missing userId claim" });

            var assigned = await _assign.GetDeviceGuidsOfUserAsync(userId, ct);

            var denied = list
                .Where(g => !assigned.Contains(g))
                .ToList();

            if (denied.Count > 0)
            {
                return StatusCode(403, new
                {
                    code = "NOT_ASSIGNED",
                    message = "Some devices are not assigned to this user",
                    denied
                });
            }
        }

        try
        {
            var resp = await _api.GetRealTimeDataAsync(list, ct);

            var returnedGuids = (resp?.data ?? new())
                .Where(x => !string.IsNullOrWhiteSpace(x.deviceGuid))
                .Select(x => x.deviceGuid!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var missingGuids = list
                .Where(g => !returnedGuids.Contains(g, StringComparer.OrdinalIgnoreCase))
                .ToList();

            return Ok(new
            {
                code = resp?.code ?? -1,
                message = resp?.message ?? resp?.msg ?? "",
                error = resp?.error,

                requestedCount = list.Count,
                returnedCount = returnedGuids.Count,
                missingCount = missingGuids.Count,

                requestedDeviceGuids = list,
                returnedDeviceGuids = returnedGuids,
                missingDeviceGuids = missingGuids,

                data = resp?.data ?? new()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5000,
                message = "Realtime failed",
                detail = ex.Message,
                requestedDeviceGuids = list
            });
        }
    }

    private string? GetUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? User.FindFirstValue("sub")
              ?? User.FindFirstValue("uid");

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private bool IsAdmin()
    {
        return User.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));
    }
}