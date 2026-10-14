using Elitech.Services;
using Elitech.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace Elitech.Controllers
{
    [Authorize(Roles = "User")]
    [Route("user")]
    public class UserController : Controller
    {
        private readonly ElitechDeviceAssignmentService _assign;
        private readonly IHubContext<ElitechAlarmHub> _hub;
        private readonly AccountService _accounts;

        public UserController(
            ElitechDeviceAssignmentService assign,
            IHubContext<ElitechAlarmHub> hub,
            AccountService accounts)
        {
            _assign = assign;
            _hub = hub;
            _accounts = accounts;
        }

        // Link cũ /api/elitech/user sẽ tự chuyển sang /user
        [HttpGet("/api/elitech/user")]
        public IActionResult LegacyUserPage()
        {
            return Redirect("/user");
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(CancellationToken ct)
        {
            // Username dùng để lookup profile/SMS flags
            var username = User?.Identity?.Name ?? "";

            // Device assignment dùng AccountId (NameIdentifier)
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? User.FindFirstValue("sub")
                      ?? "";

            // ===== Devices =====
            var devices = new List<object>();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var assigned = await _assign.GetByUserAsync(userId, ct);
                devices = assigned
                    .Select(x => new { x.DeviceGuid, x.DeviceName })
                    .ToList<object>();
            }
            ViewBag.Devices = devices;

            // ===== SMS flags =====
            bool canUserSubscribeSms = false;   // admin cho phép đăng ký hay không
            bool smsAlreadySubscribed = false;  // đã đăng ký xong => disable + đổi text

            if (!string.IsNullOrWhiteSpace(username))
            {
                var acc = await _accounts.GetProfileAsync(username);

                // ✅ chỉ mở khi admin bật (SmsSelfRegisterAllowed = true)
                canUserSubscribeSms = acc != null
                    && acc.IsActive
                    && acc.SmsSelfRegisterAllowed;

                var now = DateTime.UtcNow;

                // ✅ đã đăng ký xong: verified + subActive + còn hạn
                smsAlreadySubscribed = acc != null
                    && acc.IsActive
                    && acc.PhoneVerified
                    && acc.SmsSubActive
                    && acc.SmsSubExpiresAtUtc.HasValue
                    && acc.SmsSubExpiresAtUtc.Value > now;

                // optional: các flag khác để UI debug / hiển thị
                ViewBag.SmsSubActive = acc?.SmsSubActive ?? false;
                ViewBag.SmsSubExpiresAtUtc = acc?.SmsSubExpiresAtUtc;
                ViewBag.PhoneVerified = acc?.PhoneVerified ?? false;
                ViewBag.Phone = acc?.Phone;
            }

            // View dùng 2 biến này:
            ViewBag.CanUserSubscribeSms = canUserSubscribeSms;
            ViewBag.SmsAlreadySubscribed = smsAlreadySubscribed;

            return View();
        }
    }
}