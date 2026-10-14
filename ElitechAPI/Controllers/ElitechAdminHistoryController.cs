using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Staff")]
[Route("api/admin/elitech/history")]
public class ElitechAdminHistoryController : ElitechHistoryBaseController
{
    public ElitechAdminHistoryController(ElitechApiClient api) : base(api)
    {
    }

    // GET /api/admin/elitech/history?deviceGuid=xxx&lastHours=24
    [HttpGet]
    public Task<IActionResult> GetHistory(
        [FromQuery] string deviceGuid,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? lastHours,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        // Admin/Staff được xem toàn bộ thiết bị, không check assignment
        return FetchHistoryCoreAsync(deviceGuid, from, to, lastHours, limit, ct);
    }
}