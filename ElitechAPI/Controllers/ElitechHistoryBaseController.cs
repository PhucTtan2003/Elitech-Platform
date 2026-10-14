using Elitech.Services;
using Microsoft.AspNetCore.Mvc;

namespace Elitech.Controllers;

public abstract class ElitechHistoryBaseController : ControllerBase
{
    protected readonly ElitechApiClient Api;

    protected const int MaxDaysRange = 31;
    protected const int DefaultLimit = 150;

    protected ElitechHistoryBaseController(ElitechApiClient api)
    {
        Api = api;
    }

    protected async Task<IActionResult> FetchHistoryCoreAsync(
        string deviceGuid,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? lastHours,
        int? limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceGuid))
            return BadRequest(new { code = 400, message = "deviceGuid is required" });

        deviceGuid = deviceGuid.Trim();

        var end = to ?? DateTimeOffset.UtcNow;

        if (lastHours.HasValue && lastHours.Value <= 0)
            return BadRequest(new { code = 400, message = "lastHours must be > 0" });

        var start = from ?? (
            lastHours.HasValue
                ? end.AddHours(-lastHours.Value)
                : end.AddDays(-1)
        );

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

        if (take <= 0)
            take = DefaultLimit;

        if (take > 500)
            take = 500;

        try
        {
            var resp = await Api.GetHistoryDataRangeAsync(
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
}