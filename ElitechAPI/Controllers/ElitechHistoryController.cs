using System.Security.Claims;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[ApiController]
[Route("api/elitech")]
[Authorize]
public class ElitechHistoryController : ControllerBase
{
    private readonly ElitechApiClient _api;
    private readonly ElitechDeviceAssignmentService _assign;

    private const int MaxDaysRange = 31;
    private const int DefaultLimit = 150;

    public ElitechHistoryController(
        ElitechApiClient api,
        ElitechDeviceAssignmentService assign)
    {
        _api = api;
        _assign = assign;
    }

    [HttpGet("history")]
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

        var isAdmin = IsAdmin();

        // User chỉ xem device đã gán, Admin bỏ qua
        if (!isAdmin)
        {
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
        }

        var end = to ?? DateTimeOffset.UtcNow;

        if (lastHours.HasValue && lastHours.Value <= 0)
            return BadRequest(new { code = 400, message = "lastHours must be > 0" });

        var start = from ?? (lastHours.HasValue
            ? end.AddHours(-lastHours.Value)
            : end.AddDays(-1));

        if (start > end)
            (start, end) = (end, start);

        var days = (end - start).TotalDays;

        if (days > MaxDaysRange)
        {
            return BadRequest(new
            {
                code = 400,
                message = $"Date range too large. Max allowed is {MaxDaysRange} days.",
                from = start.UtcDateTime,
                to = end.UtcDateTime
            });
        }

        var take = limit.GetValueOrDefault(DefaultLimit);
        if (take <= 0) take = DefaultLimit;
        if (take > 500) take = 500;

        try
        {
            // Không truyền take vào đây vì tham số thứ 5 là delayBetweenRequestsMs
            var resp = await _api.GetHistoryDataRangeAsync(
                deviceGuid,
                start,
                end,
                ct,
                delayBetweenRequestsMs: 0
            );

            if (resp.code != 0)
            {
                return StatusCode(502, new
                {
                    resp.code,
                    message = resp.message ?? resp.msg ?? "Upstream error",
                    resp.error
                });
            }

            // Giới hạn số điểm trả về ở controller
            if (resp.data != null && resp.data.Count > take)
            {
                resp.data = resp.data
                    .OrderByDescending(x => x.monitorTime ?? 0)
                    .Take(take)
                    .OrderBy(x => x.monitorTime ?? 0)
                    .ToList();
            }

            return Ok(resp);
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, new
            {
                code = 499,
                message = "Request cancelled"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 500,
                message = "Internal error while fetching history",
                detail = ex.Message
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