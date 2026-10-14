using Elitech.Models.Sms;
using Elitech.Services;
using Elitech.Services.SmsSchedule;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Elitech.Controllers;

[ApiController]
[Authorize]
[Route("api/elitech/sms-temperature-schedules")]
public class ElitechSmsTemperatureScheduleController : ControllerBase
{
    private readonly SmsTemperatureScheduleService _scheduleService;
    private readonly SmsTemperatureSender _sender;
    private readonly ElitechDeviceAssignmentService _assign;

    public ElitechSmsTemperatureScheduleController(
        SmsTemperatureScheduleService scheduleService,
        SmsTemperatureSender sender,
        ElitechDeviceAssignmentService assign)
    {
        _scheduleService = scheduleService;
        _sender = sender;
        _assign = assign;
    }

    public class UpsertReq
    {
        public string DeviceGuid { get; set; } = "";
        public string? DeviceName { get; set; }
        public string Phone { get; set; } = "";
        public List<string> ScheduleTimes { get; set; } = new();
        public bool Enabled { get; set; } = true;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing user id" });

        var list = await _scheduleService.GetByUserAsync(userId, ct);
        return Ok(new { code = 0, data = list });
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertReq req, CancellationToken ct)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing user id" });

        if (req == null)
            return BadRequest(new { code = 400, message = "Body is required" });

        var guid = SmsTemperatureScheduleService.NormalizeGuid(req.DeviceGuid);

        if (string.IsNullOrWhiteSpace(guid))
            return BadRequest(new { code = 400, message = "DeviceGuid is required" });

        if (!User.IsInRole("Admin"))
        {
            var assigned = await _assign.IsAssignedAsync(userId, guid, ct);
            if (!assigned)
                return StatusCode(403, new { code = "NOT_ASSIGNED", message = "Device is not assigned to this user" });
        }

        var saved = await _scheduleService.UpsertAsync(
            userId: userId,
            deviceGuid: guid,
            deviceName: req.DeviceName,
            phone: req.Phone,
            scheduleTimes: req.ScheduleTimes,
            enabled: req.Enabled,
            ct: ct);

        return Ok(new { code = 0, data = saved });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing user id" });

        var removed = await _scheduleService.DeleteAsync(
            id,
            userId,
            User.IsInRole("Admin"),
            ct);

        return Ok(new { code = 0, removed });
    }

    [HttpPost("{id}/test-send")]
    public async Task<IActionResult> TestSend([FromRoute] string id, CancellationToken ct)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing user id" });

        var schedule = await _scheduleService.GetByIdAsync(id, ct);
        if (schedule == null)
            return NotFound(new { code = 404, message = "Schedule not found" });

        if (!User.IsInRole("Admin") && schedule.UserId != userId)
            return StatusCode(403, new { code = 403, message = "Forbidden" });

        var now = DateTimeOffset.UtcNow;
        var dueKey = $"TEST|{now:yyyyMMddHHmmss}";

        await _sender.SendAsync(schedule, dueKey, "TEST", ct);

        return Ok(new { code = 0, message = "Test send executed. Check SmsTemperatureSendLogs." });
    }

    private string? GetUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? User.FindFirstValue("sub");
    }
}