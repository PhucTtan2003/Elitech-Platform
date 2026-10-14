// Controllers/ElitechAlertEventsController.cs
using System.Security.Claims;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[ApiController]
[Authorize]
[Route("api/elitech")]
public class ElitechAlertEventsController : ControllerBase
{
    private readonly ElitechAlertEventService _events;
    private readonly ElitechDeviceAssignmentService _assign;

    public ElitechAlertEventsController(ElitechAlertEventService events, ElitechDeviceAssignmentService assign)
    {
        _events = events;
        _assign = assign;
    }

    // GET /api/elitech/alert-events?deviceGuid=...&from=...&to=...&page=1&pageSize=50
    [HttpGet("alert-events")]
    public async Task<IActionResult> GetAlertEvents(
        [FromQuery] string? deviceGuid,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        var isAdmin = User.IsInRole("Admin");

        if (!isAdmin && string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing userId claim" });

        // USER: nếu có lọc theo deviceGuid => phải được gán
        if (!isAdmin && !string.IsNullOrWhiteSpace(deviceGuid))
        {
            var ok = await _assign.IsAssignedAsync(userId, deviceGuid.Trim(), ct);
            if (!ok)
                return StatusCode(403, new { code = "NOT_ASSIGNED", message = "Device is not assigned to this user" });
        }

        DateTime? fromUtc = from?.UtcDateTime;
        DateTime? toUtc = to?.UtcDateTime;

        var (items, total) = await _events.QueryAsync(
            userId: isAdmin ? null : userId,
            isAdmin: isAdmin,
            deviceGuid: deviceGuid,
            fromUtc: fromUtc,
            toUtc: toUtc,
            page: page,
            pageSize: pageSize,
            ct: ct);

        return Ok(new
        {
            code = 0,
            page,
            pageSize,
            total,
            data = items
        });
    }
}