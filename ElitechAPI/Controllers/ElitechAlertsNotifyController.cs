using System.Security.Claims;

using Elitech.Data;

using Elitech.Models.Alerts;
using Elitech.Models.RealtimeData;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;

using Microsoft.AspNetCore.Mvc;

using MongoDB.Driver;



namespace Elitech.Controllers;



[ApiController]

[Route("api/elitech/alerts")]

[Authorize]

public class ElitechAlertsNotifyController : ControllerBase

{

    private readonly IMongoCollection<ElitechAlertEvent> _events;
    private readonly AccountService _accounts;


    public ElitechAlertsNotifyController(MongoContext db, AccountService accounts)

    {
        _accounts = accounts;
        _events = db.Database.GetCollection<ElitechAlertEvent>("AlertEvents");

    }



    // GET /api/elitech/alerts/unread?take=10

    [HttpGet("unread")]

    public async Task<IActionResult> Unread([FromQuery] int take = 10, CancellationToken ct = default)

    {

        take = Math.Clamp(take, 1, 50);



        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(userId))

            return Unauthorized(new { code = 401, message = "No userId claim" });



        var list = await _events.Find(x => x.UserId == userId && !x.IsRead)

            .SortByDescending(x => x.OccurredAtUtc)

            .Limit(take)

            .ToListAsync(ct);



        return Ok(new { data = list });

    }



    // POST /api/elitech/alerts/mark-read?id=...

    [HttpPost("mark-read")]
    public async Task<IActionResult> MarkRead([FromQuery] string id, CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "No userId claim" });

        if (!MongoDB.Bson.ObjectId.TryParse(id, out var oid))
            return BadRequest(new { message = "invalid id" });

        var res = await _events.UpdateOneAsync(
            x => x.Id == oid && x.UserId == userId,
            Builders<ElitechAlertEvent>.Update.Set(x => x.IsRead, true),
            cancellationToken: ct
        );

        return Ok(new { ok = res.ModifiedCount > 0 });
    }



    // POST /api/elitech/alerts/mark-all-read

    [HttpPost("mark-all-read")]

    public async Task<IActionResult> MarkAllRead(CancellationToken ct = default)

    {

        var userId = GetUserId();

        if (string.IsNullOrWhiteSpace(userId))

            return Unauthorized(new { code = 401, message = "No userId claim" });



        var res = await _events.UpdateManyAsync(

            x => x.UserId == userId && !x.IsRead,

            Builders<ElitechAlertEvent>.Update.Set(x => x.IsRead, true),

            cancellationToken: ct);



        return Ok(new { ok = true, updated = res.ModifiedCount });

    }



    private string? GetUserId()

    {

        var id = User.FindFirstValue(ClaimTypes.NameIdentifier);

        id ??= User.FindFirstValue("sub");

        return string.IsNullOrWhiteSpace(id) ? null : id;

    }
    [HttpGet("my-phone")]
    public async Task<IActionResult> MyPhone(CancellationToken ct = default)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized(new { code = 401, message = "No userId claim" });

        var phone = await _accounts.GetPhoneByUserIdAsync(userId);
        return Ok(new { userId, phone });
    }

    private static long? TryParseTs(object? v)
    {
        if (v == null) return null;

        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case double d: return (long)d;
            case decimal m: return (long)m;
            case string s:
                if (string.IsNullOrWhiteSpace(s)) return null;
                if (double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dd))
                    return (long)dd;
                if (long.TryParse(s, out var ll)) return ll;
                return null;
            default:
                // fallback ToString
                var str = v.ToString();
                if (string.IsNullOrWhiteSpace(str)) return null;
                if (double.TryParse(str, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d2))
                    return (long)d2;
                if (long.TryParse(str, out var l2)) return l2;
                return null;
        }
    }

    //// ✅ Ưu tiên field nào “đang chạy” theo realtime
    //private static long? TryGetSampleTs(RealTimeItem row)
    //{
    //    try
    //    {
    //        // ✅ Ưu tiên các field phản ánh thời điểm đo/đẩy data gần nhất
    //        long? ts =
    //            row.uploadTime ??

    //            row.lastSessionTime ??

    //            row.lastAddressTime;

    //        if (!ts.HasValue) return null;

    //        var v = ts.Value;

    //        // normalize ms -> s
    //        if (v > 10_000_000_000L) v /= 1000;

    //        // guard: nếu v quá nhỏ bất thường thì bỏ (optional)
    //        if (v < 1_000_000_000L) return null;

    //        return v;
    //    }
    //    catch
    //    {
    //        return null;
    //    }
    //}


}
