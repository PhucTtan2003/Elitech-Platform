using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Elitech.Services;
using Elitech.Models;

namespace Elitech.Controllers;

[ApiController]
[Route("api/elitech")]
[Authorize]
public class ElitechMyDevicesController : ControllerBase
{
    private readonly ElitechDeviceAssignmentService _assign;
    private readonly ElitechApiClient _api;

    public ElitechMyDevicesController(
        ElitechDeviceAssignmentService assign,
        ElitechApiClient api)
    {
        _assign = assign;
        _api = api;
    }

    [HttpGet("my-devices")]
    public async Task<IActionResult> MyDevices(CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "Missing NameIdentifier claim" });

        var list = await _assign.GetByUserAsync(userId, ct)
                   ?? new List<ElitechDeviceAssignment>();

        var data = list
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceGuid))
            .Select(x => new
            {
                deviceGuid = x.DeviceGuid,
                deviceName = string.IsNullOrWhiteSpace(x.DeviceName) ? x.DeviceGuid : x.DeviceName
            })
            .DistinctBy(x => x.deviceGuid)
            .ToList();

        return Ok(new { code = 0, data });
    }

    [HttpGet("all-devices")]
    public async Task<IActionResult> AllDevices(CancellationToken ct)
    {
        if (!IsAdmin())
        {
            return StatusCode(403, new
            {
                code = 403,
                message = "Only Admin can view all devices"
            });
        }

        try
        {
            var guidResp = await _api.GetDeviceGuidsAsync(ct);

            if (guidResp == null)
                return StatusCode(502, new { code = -1, message = "Empty Elitech response" });

            if (guidResp.Code != 0)
            {
                return StatusCode(502, new
                {
                    code = guidResp.Code,
                    message = guidResp.Msg ?? guidResp.Message ?? guidResp.Error ?? "Elitech upstream error",
                    error = guidResp.Error
                });
            }

            var guids = (guidResp.Data ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (guids.Count == 0)
                return Ok(new { code = 0, data = new List<object>() });

            var result = guids.Select(g => new
            {
                deviceGuid = g,
                deviceName = g
            }).ToList();

            return Ok(new { code = 0, data = result });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5001,
                message = "all-devices failed",
                detail = ex.Message
            });
        }
    }

    private bool IsAdmin()
    {
        return User.Claims.Any(c =>
            c.Type == ClaimTypes.Role &&
            string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));
    }
}