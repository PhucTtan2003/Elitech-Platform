using Elitech.Models;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Elitech.Controllers;

[Authorize]
[Route("SmsPeriodic")]
public class SmsPeriodicController : Controller
{
    private readonly AccountService _accounts;

    public SmsPeriodicController(AccountService accounts)
    {
        _accounts = accounts;
    }

    // Hỗ trợ cả /SmsPeriodic và /SmsPeriodic/Index
    [HttpGet("")]
    [HttpGet("Index")]
    public IActionResult Index()
    {
        return View();
    }

    // Nếu còn link cũ thì vẫn chạy
    [HttpGet("/Sms/TemperatureSchedules")]
    public IActionResult TemperatureSchedules()
    {
        return View("Index");
    }

    // GET /SmsPeriodic/status
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var username = User?.Identity?.Name ?? "";
        if (string.IsNullOrWhiteSpace(username))
            return Unauthorized(new { ok = false, message = "Chưa đăng nhập." });

        var acc = await _accounts.GetProfileAsync(username);
        if (acc == null)
            return Unauthorized(new { ok = false, message = "Không tìm thấy tài khoản." });

        var now = DateTime.UtcNow;

        var phoneNumbers = GetAccountPhoneNumbers(acc);

        var smsStatus =
            (acc.SmsSubActive && acc.SmsSubExpiresAtUtc.HasValue && acc.SmsSubExpiresAtUtc.Value > now)
                ? "ACTIVE"
                : (acc.SmsSubExpiresAtUtc.HasValue && acc.SmsSubExpiresAtUtc.Value <= now)
                    ? "EXPIRED"
                    : "OFF";

        var canUsePeriodicSms =
            acc.SmsSelfRegisterAllowed &&
            acc.SmsAlertEnabled &&
            acc.SmsSubActive &&
            acc.SmsSubExpiresAtUtc.HasValue &&
            acc.SmsSubExpiresAtUtc.Value > now &&
            acc.PhoneVerified &&
            phoneNumbers.Count > 0;

        string reason;

        if (!acc.SmsSelfRegisterAllowed)
            reason = "Admin đã khóa tính năng SMS định kỳ.";
        else if (!acc.SmsAlertEnabled)
            reason = "SMS đang bị tắt.";
        else if (!acc.SmsSubActive)
            reason = "Dịch vụ SMS chưa được bật.";
        else if (!acc.SmsSubExpiresAtUtc.HasValue)
            reason = "Dịch vụ SMS chưa có ngày hết hạn.";
        else if (acc.SmsSubExpiresAtUtc.Value <= now)
            reason = "Dịch vụ SMS đã hết hạn.";
        else if (phoneNumbers.Count == 0)
            reason = "Bạn chưa thêm số điện thoại trong Profile.";
        else if (!acc.PhoneVerified)
            reason = "Bạn chưa xác thực số điện thoại trong Profile.";
        else
            reason = "Có thể sử dụng SMS định kỳ.";

        return Ok(new
        {
            ok = true,
            username = acc.Username,

            // Giữ lại field cũ để view cũ không vỡ
            phone = phoneNumbers.FirstOrDefault(),

            // Field mới: view SMS định kỳ sẽ dùng danh sách này
            phoneNumbers = phoneNumbers,

            phoneVerified = acc.PhoneVerified,

            smsSelfRegisterAllowed = acc.SmsSelfRegisterAllowed,
            smsAlertEnabled = acc.SmsAlertEnabled,
            smsSubActive = acc.SmsSubActive,
            smsSubStartedAtUtc = acc.SmsSubStartedAtUtc,
            smsSubExpiresAtUtc = acc.SmsSubExpiresAtUtc,

            status = smsStatus,
            canUsePeriodicSms,
            reason
        });
    }

    // =========================
    // Helpers
    // =========================

    private static List<string> GetAccountPhoneNumbers(AccountViewModel acc)
    {
        var result = new List<string>();

        // Ưu tiên mảng phones trong Mongo
        if (acc.Phones != null && acc.Phones.Count > 0)
        {
            foreach (var p in acc.Phones)
            {
                var norm = NormalizeVnPhone(p);
                if (!string.IsNullOrWhiteSpace(norm))
                    result.Add(norm);
            }
        }

        // Fallback field phone cũ
        var single = NormalizeVnPhone(acc.Phone);
        if (!string.IsNullOrWhiteSpace(single))
            result.Add(single);

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    private static void AddPhoneValue(List<string> result, object? value)
    {
        if (value == null) return;

        if (value is string s)
        {
            AddPhoneString(result, s);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                AddPhoneString(result, item.ToString() ?? "");
            }
        }
    }

    private static void AddPhoneString(List<string> result, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;

        // Nếu 1 field chứa nhiều số, ví dụ: "0987..., 0912..."
        var parts = raw.Split(
            new[] { ',', ';', '|', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var part in parts)
        {
            var norm = NormalizeVnPhone(part);
            if (!string.IsNullOrWhiteSpace(norm))
                result.Add(norm);
        }
    }

    private static string? NormalizeVnPhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim();

        s = Regex.Replace(s, @"[\s\-\.\(\)]", "");

        if (s.StartsWith("+"))
            s = s[1..];

        if (s.StartsWith("0084"))
            s = "84" + s[4..];

        if (s.StartsWith("0") && s.Length >= 10)
            s = "84" + s[1..];

        if (!s.All(char.IsDigit))
            return null;

        // VN thường: 84 + 9 số = 11 ký tự, một số case có thể 12
        if (!s.StartsWith("84"))
            return null;

        if (s.Length < 11 || s.Length > 12)
            return null;

        return s;
    }
}