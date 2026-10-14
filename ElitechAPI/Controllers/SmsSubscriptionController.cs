using Elitech.Hubs;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Elitech.Controllers;

[Authorize]
public class SmsSubscriptionController : Controller
{
    private readonly AccountService _accounts;
    private readonly AlertSmsService _smsAlert;
    private readonly IHubContext<SmsHub> _smsHub;

    public SmsSubscriptionController(AccountService accounts, AlertSmsService smsAlert, IHubContext<SmsHub> smsHub)
    {
        _accounts = accounts;
        _smsAlert = smsAlert;
        _smsHub = smsHub;
    }

    [HttpPost("/Sms/send-code")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendCode(string phone, CancellationToken ct)
    {
        var username = User?.Identity?.Name ?? "";
        if (string.IsNullOrWhiteSpace(username)) return Unauthorized();

        var acc = await _accounts.GetProfileAsync(username);
        if (acc == null) return Unauthorized();
        if (!acc.SmsSelfRegisterAllowed)
        {
            return StatusCode(403, new
            {
                ok = false,
                message = "Tính năng SMS đã bị Admin khóa. Vui lòng liên hệ Admin để mở lại."
            });
        }
        var norm = NormalizeVnPhone(phone);
        if (string.IsNullOrWhiteSpace(norm) || norm.Length < 10)
            return BadRequest(new { ok = false, message = "Số điện thoại không hợp lệ." });

        var now = DateTime.UtcNow;
        if (acc.PhoneVerifyLastSentAtUtc.HasValue &&
            (now - acc.PhoneVerifyLastSentAtUtc.Value) < TimeSpan.FromSeconds(60))
        {
            return BadRequest(new { ok = false, message = "Vui lòng thử lại sau 60 giây." });
        }

        var code = new Random().Next(100000, 999999).ToString();
        var hash = BCrypt.Net.BCrypt.HashPassword(code);
        var expires = now.AddMinutes(5);

        await _accounts.SetPhoneVerifyPendingAsync(username, norm, hash, expires, now, ct);

        var msg = $"SMARTTECH: Ma OTP cua ban la {code}. Hieu luc 5 phut.";
        var requestId = $"sms-reg-{acc.Id}-{Guid.NewGuid():N}";

        var (ok, reason) = await _smsAlert.SendToPhoneRawAsync(norm, msg, requestId, ct);
        if (!ok) return StatusCode(500, new { ok = false, message = $"Gửi SMS thất bại: {reason}" });

        return Ok(new { ok = true, message = "Đã gửi OTP." });
    }

    [HttpPost("/Sms/verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string code, CancellationToken ct)
    {
        var username = User?.Identity?.Name ?? "";
        if (string.IsNullOrWhiteSpace(username)) return Unauthorized();

        var acc = await _accounts.GetProfileAsync(username);
        if (acc == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(acc.PhoneVerifyCodeHash) ||
            !acc.PhoneVerifyExpiresAtUtc.HasValue ||
            acc.PhoneVerifyExpiresAtUtc.Value < DateTime.UtcNow)
        {
            return BadRequest(new { ok = false, message = "OTP hết hạn hoặc chưa gửi. Vui lòng gửi lại." });
        }

        code = (code ?? "").Trim();
        if (!BCrypt.Net.BCrypt.Verify(code, acc.PhoneVerifyCodeHash))
            return BadRequest(new { ok = false, message = "OTP sai." });

        await _accounts.MarkPhoneVerifiedAsync(username, ct);

        // trả về phone để UI show ngay
        var updated = await _accounts.GetProfileAsync(username);
        return Ok(new { ok = true, phone = updated?.Phone, message = "Xác thực thành công. Đã bật SMS cảnh báo." });
    }
    [HttpPost("/Admin/Sms/ClearSms")]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Clear([FromForm] string username, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(username))
                return Json(new { ok = false, reason = "username NULL" });

            await _accounts.ClearSmsSubscriptionAsync(username, ct);

            return Json(new { ok = true });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, reason = ex.Message });
        }
    }
    [HttpPost("/Sms/unsubscribe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsubscribe(CancellationToken ct)
    {
        var username = User?.Identity?.Name ?? "";
        if (string.IsNullOrWhiteSpace(username)) return Unauthorized();

        var acc = await _accounts.GetProfileAsync(username);
        if (acc == null) return Unauthorized();

        await _accounts.ClearSmsSubscriptionAsync(username, ct);

        return Ok(new
        {
            ok = true,
            message = "Đã hủy dịch vụ SMS. Trạng thái: chưa đăng ký."
        });
    }

    // --- helpers ---
    private static string NormalizeVnPhone(string raw)
    {
        var s = (raw ?? "").Trim();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s|\-|\.", "");
        if (s.StartsWith("+84")) s = "84" + s.Substring(3);
        if (s.StartsWith("0") && s.Length >= 10) s = "84" + s.Substring(1);
        return s;
    }
}
