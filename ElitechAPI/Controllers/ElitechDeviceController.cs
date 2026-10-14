using Elitech.Models.DeviceParams;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api/elitech")]
public class ElitechDeviceController : ControllerBase
{
    private readonly ElitechApiClient _api;
    private readonly ElitechDeviceAssignmentService _assign;
    private readonly ElitechDeviceParamMappingService _paramMappings;

    public ElitechDeviceController(
        ElitechApiClient api,
        ElitechDeviceAssignmentService assign,
        ElitechDeviceParamMappingService paramMappings)
    {
        _api = api;
        _assign = assign;
        _paramMappings = paramMappings;
    }

    [HttpGet("device-info")]
    public async Task<IActionResult> GetDeviceInfo(
        [FromQuery] string deviceGuids,
        CancellationToken ct)
    {
        var list = (deviceGuids ?? "")
            .Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (list.Length == 0)
        {
            return BadRequest(new
            {
                code = 400,
                message = "deviceGuids required (comma separated)"
            });
        }

        try
        {
            var resp = await _api.GetDeviceInfoAsync(list, ct);

            if (resp?.code is null or not 0)
            {
                return StatusCode(502, new
                {
                    code = resp?.code ?? -1,
                    message = resp?.msg ??
                              resp?.message ??
                              "Upstream error"
                });
            }

            var data = resp.data?.Select(x => new
            {
                deviceName = x.deviceName,
                deviceGuid = x.deviceGuid,
                deviceTypeName = x.deviceTypeName,
                sceneName = x.sceneName,
                subUid = x.subUid,
                smsCount = x.smsCount,
                voiceCount = x.voiceCount,
                expiredTime = x.expiredTime,
                lastTime = x.lastTime,
                lastTimeStr = x.lastTime.HasValue
                    ? DateTimeOffset
                        .FromUnixTimeSeconds(x.lastTime.Value)
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss")
                    : null
            });

            return Ok(new { code = 0, data });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new
            {
                code = 502,
                message = "Upstream HTTP error",
                detail = ex.Message
            });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return StatusCode(500, new
            {
                code = 5001,
                message = "JSON parse error",
                detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5000,
                message = ex.Message
            });
        }
    }

    // =========================
    // API #7 - Get Device GUIDs
    // GET /api/elitech/device-guids
    // =========================
    [HttpGet("device-guids")]
    public async Task<IActionResult> GetDeviceGuids(CancellationToken ct)
    {
        try
        {
            var resp = await _api.GetDeviceGuidsAsync(ct);

            if (resp is null)
            {
                return StatusCode(502, new
                {
                    code = -1,
                    message = "Empty upstream response"
                });
            }

            if (resp.Code != 0)
            {
                return StatusCode(502, new
                {
                    code = resp.Code,
                    message = resp.Msg ??
                              resp.Message ??
                              "Upstream error",
                    resp.Error
                });
            }

            return Ok(new
            {
                code = 0,
                data = resp.Data ?? new List<string>()
            });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new
            {
                code = 502,
                message = "Upstream HTTP error",
                detail = ex.Message
            });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return StatusCode(500, new
            {
                code = 5001,
                message = "JSON parse error",
                detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5000,
                message = ex.Message
            });
        }
    }

    // =========================================================
    // ELITECH V2 - QUERY DEVICE PARAMS
    // Chỉ đọc từ Elitech V2, chưa ghi Mongo.
    //
    // GET /api/elitech/device-params?deviceGuids=GUID1,GUID2
    // =========================================================
    [Authorize]
    [HttpGet("device-params")]
    public async Task<IActionResult> GetDeviceParams(
        [FromQuery] string deviceGuids,
        CancellationToken ct)
    {
        var guids = ParseGuids(deviceGuids);

        if (guids.Count == 0)
        {
            return BadRequest(new
            {
                code = 400,
                message = "deviceGuids is required"
            });
        }

        var accessError = await CheckDeviceAccessAsync(guids, ct);
        if (accessError != null)
            return accessError;

        try
        {
            var resp = await _api.GetDeviceParamsAsync(guids, ct);

            if (resp.code != 0)
            {
                return StatusCode(502, new
                {
                    resp.code,
                    message = resp.message ??
                              resp.msg ??
                              "Elitech V2 upstream error",
                    resp.error
                });
            }

            return Ok(resp);
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new
            {
                code = 502,
                message = "Elitech V2 HTTP error",
                detail = ex.Message
            });
        }
        catch (System.Text.Json.JsonException ex)
        {
            return StatusCode(500, new
            {
                code = 5001,
                message = "Elitech V2 JSON parse error",
                detail = ex.Message
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5000,
                message = "Get device params failed",
                detail = ex.Message
            });
        }
    }

    public sealed class SyncDeviceParamsRequest
    {
        public List<string> DeviceGuids { get; set; } = new();
    }

    // =========================================================
    // ELITECH V2 -> MONGO
    // Lấy getParams V2 và lưu vào collection DeviceParamMappings.
    //
    // POST /api/elitech/device-params/sync
    // Body: { "deviceGuids": ["GUID1"] }
    // =========================================================
    [Authorize]
    [HttpPost("device-params/sync")]
    public async Task<IActionResult> SyncDeviceParams(
        [FromBody] SyncDeviceParamsRequest? req,
        CancellationToken ct)
    {
        var guids = NormalizeGuids(req?.DeviceGuids);

        if (guids.Count == 0)
        {
            return BadRequest(new
            {
                code = 400,
                message = "deviceGuids is required"
            });
        }

        var accessError = await CheckDeviceAccessAsync(guids, ct);
        if (accessError != null)
            return accessError;

        try
        {
            var resp = await _api.GetDeviceParamsAsync(guids, ct);

            if (resp.code != 0)
            {
                return StatusCode(502, new
                {
                    resp.code,
                    message = resp.message ??
                              resp.msg ??
                              "Elitech V2 upstream error",
                    resp.error
                });
            }

            var saved = await _paramMappings.UpsertFromApiAsync(resp, ct);

            var mappings = new List<ElitechDeviceParamMapping>();

            foreach (var guid in guids)
            {
                mappings.AddRange(
                    await _paramMappings.GetByDeviceAsync(
                        guid,
                        subUid: null,
                        ct));
            }

            return Ok(new
            {
                code = 0,
                message = resp.message ?? "success",
                syncedDevices = guids.Count,
                savedParams = saved,
                data = resp.data,
                mappings
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                code = 5000,
                message = "Sync device params failed",
                detail = ex.Message
            });
        }
    }

    // =========================================================
    // Đọc mapping đã lưu trong Mongo.
    //
    // GET /api/elitech/device-param-mappings
    //     ?deviceGuid=GUID&subUid=0
    // =========================================================
    [Authorize]
    [HttpGet("device-param-mappings")]
    public async Task<IActionResult> GetDeviceParamMappings(
        [FromQuery] string deviceGuid,
        [FromQuery] int? subUid,
        CancellationToken ct)
    {
        var guid = NormalizeGuid(deviceGuid);

        if (string.IsNullOrWhiteSpace(guid))
        {
            return BadRequest(new
            {
                code = 400,
                message = "deviceGuid is required"
            });
        }

        var accessError = await CheckDeviceAccessAsync(
            new List<string> { guid },
            ct);

        if (accessError != null)
            return accessError;

        var data = await _paramMappings.GetByDeviceAsync(
            guid,
            subUid,
            ct);

        return Ok(new
        {
            code = 0,
            total = data.Count,
            data
        });
    }

    public sealed class ManualDeviceParamMappingRequest
    {
        public string DeviceGuid { get; set; } = "";
        public int SubUid { get; set; }
        public string ParamCode { get; set; } = "";
        public string? LegacyField { get; set; }
    }

    // =========================================================
    // Admin/Staff chỉnh mapping khi AUTO chưa đúng.
    // Mapping MANUAL sẽ không bị ghi đè ở lần sync tiếp theo.
    //
    // POST /api/elitech/device-param-mappings/manual
    // =========================================================
    [Authorize(Roles = "Admin,Staff")]
    [HttpPost("device-param-mappings/manual")]
    public async Task<IActionResult> SetManualDeviceParamMapping(
        [FromBody] ManualDeviceParamMappingRequest? req,
        CancellationToken ct)
    {
        if (req == null)
        {
            return BadRequest(new
            {
                code = 400,
                message = "Request body is required"
            });
        }

        try
        {
            var updated =
                await _paramMappings.SetManualLegacyFieldAsync(
                    req.DeviceGuid,
                    req.SubUid,
                    req.ParamCode,
                    req.LegacyField,
                    ct);

            if (!updated)
            {
                return NotFound(new
                {
                    code = 404,
                    message = "Mapping not found. Sync device params first."
                });
            }

            var item = await _paramMappings.GetOneAsync(
                req.DeviceGuid,
                req.SubUid,
                req.ParamCode,
                ct);

            return Ok(new
            {
                code = 0,
                message = "Manual mapping updated",
                data = item
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new
            {
                code = 400,
                message = ex.Message
            });
        }
    }

    private async Task<IActionResult?> CheckDeviceAccessAsync(
        List<string> guids,
        CancellationToken ct)
    {
        if (CanViewAllDevices())
            return null;

        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized(new
            {
                code = 401,
                message = "Missing user id claim"
            });
        }

        var assigned = await _assign
            .GetDeviceGuidsOfUserAsync(userId, ct);

        var denied = guids
            .Where(g => !assigned.Contains(g))
            .ToList();

        if (denied.Count == 0)
            return null;

        return StatusCode(403, new
        {
            code = "NOT_ASSIGNED",
            message = "Some devices are not assigned to this user",
            denied
        });
    }

    private static List<string> ParseGuids(string? value)
    {
        return NormalizeGuids(
            (value ?? "").Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }

    private static List<string> NormalizeGuids(
        IEnumerable<string>? values)
    {
        return (values ?? Enumerable.Empty<string>())
            .Select(NormalizeGuid)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeGuid(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim().ToUpperInvariant();

    private string? GetUserId()
    {
        var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? User.FindFirstValue("sub")
                 ?? User.FindFirstValue("uid");

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private bool CanViewAllDevices()
    {
        return User.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || c.Type == "role") &&
            (string.Equals(
                 c.Value,
                 "Admin",
                 StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 c.Value,
                 "Staff",
                 StringComparison.OrdinalIgnoreCase)));
    }
}