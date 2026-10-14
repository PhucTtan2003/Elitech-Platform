using System.Security.Claims;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[ApiController]
[Authorize(Roles = "User")]
[Route("api/user/elitech/history")]
public class ElitechUserHistoryController : ElitechHistoryBaseController
{
    private readonly ElitechDeviceAssignmentService _assign;

    public ElitechUserHistoryController(
        ElitechApiClient api,
        ElitechDeviceAssignmentService assign) : base(api)
    {
        _assign = assign;
    }

    // GET /api/user/elitech/history?deviceGuid=xxx&lastHours=24
    [HttpGet]
    public async Task<IActionResult> GetHistory(
        [FromQuery] string deviceGuid,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? lastHours,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            return BadRequest(new { code = 400, message = "deviceGuid is required" });

        deviceGuid = deviceGuid.Trim();

        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "No userId claim" });

        var ok = await _assign.IsAssignedAsync(userId, deviceGuid, ct);

        if (!ok)
        {
            return StatusCode(403, new
            {
                code = "NOT_ASSIGNED",
                message = "Device is not assigned to this user",
                deviceGuid,
                userId
            });
        }

        return await FetchHistoryCoreAsync(deviceGuid, from, to, lastHours, limit, ct);
    }

    private string? GetUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
              ?? User.FindFirstValue("sub")
              ?? User.FindFirstValue("uid");

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }
}