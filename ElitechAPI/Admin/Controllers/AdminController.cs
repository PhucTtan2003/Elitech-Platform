using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Elitech.Data;
using Elitech.Models;
using Elitech.Services;

using MongoDB.Driver;
using MongoDB.Bson;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Elitech.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("Admin")]
    public class AdminController : Controller
    {
        private readonly AccountService _accounts;
        private readonly ElitechDeviceAssignmentService _assign;

        // from ElitechAdminController
        private readonly ElitechApiClient _api;
        private readonly MongoContext _db;
        private readonly ILogger<AdminController> _logger;
        private readonly IWebHostEnvironment _env;

        public AdminController(
            AccountService accounts,
            ElitechDeviceAssignmentService assign,
            ElitechApiClient api,
            MongoContext db,
            ILogger<AdminController> logger,
            IWebHostEnvironment env)
        {
            _accounts = accounts;
            _assign = assign;

            _api = api;
            _db = db;
            _logger = logger;
            _env = env;
        }

        // =========================
        // VIEWS
        // =========================
        [HttpGet("")]
        public IActionResult Index() => View();

        [HttpGet("AssignDevices")]
        public IActionResult AssignDevices() => View();

        [HttpGet("CreateUser")]
        public IActionResult CreateUser() => View();

        [HttpGet("SmsSubscriptions")]
        public IActionResult SmsSubscriptions() => View(); // Views/Admin/SmsSubscriptions.cshtml

        // =========================
        // (THÊM TỪ ĐOẠN 1) VIEW: Accounts
        // =========================
        [HttpGet("Accounts")]
        public IActionResult Accounts() => View();

        // =========================
        // CREATE USER (POST)
        // =========================
        [HttpPost("CreateUser")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateUser(CreateUserViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Username) ||
                string.IsNullOrWhiteSpace(model.Password))
            {
                ModelState.AddModelError("", "Vui lòng nhập đầy đủ thông tin.");
                return View(model);
            }

            model.Username = model.Username.Trim();

            // ✅ Validate phone (optional)
            string? NormalizeVnPhone(string? phone)
            {
                if (string.IsNullOrWhiteSpace(phone)) return null;

                var raw = phone.Trim()
                    .Replace(" ", "")
                    .Replace("-", "")
                    .Replace(".", "");

                if (raw.StartsWith("+")) raw = raw[1..];

                if (raw.StartsWith("0"))
                    raw = "84" + raw[1..];

                if (!raw.StartsWith("84")) return null;
                if (!raw.All(char.IsDigit)) return null;
                if (raw.Length < 11 || raw.Length > 12) return null;

                return raw;
            }

            var phoneNorm = NormalizeVnPhone(model.Phone);
            if (!string.IsNullOrWhiteSpace(model.Phone) && phoneNorm is null)
            {
                ModelState.AddModelError("", "Số điện thoại không hợp lệ. VD: 0987654321 hoặc +84987654321");
                return View(model);
            }

            var existing = await _accounts.GetByUsernameAsync(model.Username);
            if (existing != null)
            {
                ModelState.AddModelError("", "Username đã tồn tại.");
                return View(model);
            }

            await _accounts.CreateAsync(
                username: model.Username,
                password: model.Password,
                role: model.Role
            );

            TempData["Success"] = "Tạo tài khoản thành công.";
            return RedirectToAction("Index");
        }


        // =========================
        // ADMIN JSON API (under /Admin)
        // =========================
        [HttpGet("GetUsers")]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _accounts.GetByRoleAsync("User");
            var data = users.Select(u => new
            {
                userId = u.Id,
                username = u.Username
            });

            return Json(new { code = 0, data });
        }

        [HttpGet("GetAssignments")]
        public async Task<IActionResult> GetAssignments(
            [FromQuery] string userId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return Json(new { code = 1, message = "userId required" });

            var list = await _assign.GetByUserAsync(userId, ct);
            var data = list.Select(x => new
            {
                deviceGuid = x.DeviceGuid,
                deviceName = x.DeviceName,
                assignedAtUtc = x.AssignedAtUtc
            });

            return Json(new { code = 0, data });
        }

        [HttpPost("AssignDevices")]
        public async Task<IActionResult> AssignDevice(
            [FromBody] AssignReq? req,
            CancellationToken ct)
        {
            if (req is null)
                return BadRequest(new { code = 1, message = "Body required (application/json)" });

            if (string.IsNullOrWhiteSpace(req.UserId) ||
                string.IsNullOrWhiteSpace(req.DeviceGuid))
                return BadRequest(new { code = 1, message = "Invalid payload" });

            await _assign.AssignAsync(req.UserId, req.DeviceGuid, req.DeviceName, ct);
            return Ok(new { code = 0 });
        }

        [HttpPost("UnassignDevice")]
        public async Task<IActionResult> UnassignDevice(
            [FromBody] AssignReq? req,
            CancellationToken ct)
        {
            if (req is null)
                return BadRequest(new { code = 1, message = "Body required (application/json)" });

            if (string.IsNullOrWhiteSpace(req.UserId) ||
                string.IsNullOrWhiteSpace(req.DeviceGuid))
                return BadRequest(new { code = 1, message = "Invalid payload" });

            await _assign.UnassignAsync(req.UserId, req.DeviceGuid, ct);
            return Ok(new { code = 0 });
        }

        public record AssignReq(string UserId, string DeviceGuid, string? DeviceName);

        // =========================
        // UPSTREAM ELITECH API #8
        // =========================
        public class AddDeviceReq
        {
            public string DeviceGuid { get; set; } = "";
            public string DeviceName { get; set; } = "";
        }

        [HttpPost("/api/admin/elitech/add-device")]
        public async Task<IActionResult> AddDevice(
            [FromBody] AddDeviceReq req,
            CancellationToken ct)
        {
            var errorId = Guid.NewGuid().ToString("N");

            try
            {
                if (req == null)
                    return BadRequest(new { code = 400, message = "Request body is required" });

                if (string.IsNullOrWhiteSpace(req.DeviceGuid))
                    return BadRequest(new { code = 400, message = "deviceGuid is required" });

                var deviceGuid = req.DeviceGuid.Trim();
                var deviceName = string.IsNullOrWhiteSpace(req.DeviceName)
                    ? $"Device {deviceGuid}"
                    : req.DeviceName.Trim();

                AddDeviceResp? resp;
                try
                {
                    resp = await _api.AddDeviceAsync(new[]
                    {
                        new AddDeviceInfo(deviceName, deviceGuid)
                    }, ct);
                }
                catch (Exception exUp)
                {
                    _logger.LogError(exUp,
                        "[{ErrorId}] Elitech addDevice failed. guid={Guid}, name={Name}",
                        errorId, deviceGuid, deviceName);

                    return StatusCode(502, BuildError(
                        errorId: errorId,
                        message: "Thêm Thiết Bị Thất Bại. Vui Lòng Thử Lại!",
                        ex: exUp,
                        upstreamBody: TryExtractUpstreamBody(exUp),
                        upstreamStatus: TryExtractUpstreamStatus(exUp)
                    ));
                }

                if (resp == null || resp.code != 0)
                {
                    _logger.LogWarning(
                        "[{ErrorId}] Elitech addDevice returned error. guid={Guid}, name={Name}, code={Code}, msg={Msg}",
                        errorId, deviceGuid, deviceName, resp?.code, resp?.msg ?? resp?.message);

                    return StatusCode(502, new
                    {
                        errorId,
                        code = resp?.code ?? -1,
                        message = resp?.msg ?? resp?.message ?? "Upstream error",
                        upstreamError = resp?.error,
                        deviceGuid,
                        deviceName,
                        time = resp?.time
                    });
                }

                try
                {
                    await _db.Devices.ReplaceOneAsync(
                        x => x.DeviceGuid == deviceGuid,
                        new DeviceRegistry
                        {
                            DeviceGuid = deviceGuid,
                            DeviceName = deviceName,
                            IsActive = true,
                            AddedBy = User.Identity?.Name ?? "admin",
                            AddedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        },
                        new ReplaceOptions { IsUpsert = true },
                        ct
                    );
                }
                catch (Exception exDb)
                {
                    _logger.LogError(exDb,
                        "[{ErrorId}] Đăng ký thiết bị vào MongoDB thất bại. guid={Guid}, name={Name}",
                        errorId, deviceGuid, deviceName);

                    return StatusCode(500, BuildError(
                        errorId: errorId,
                        message: "Đăng ký thiết bị vào MongoDB thất bại",
                        ex: exDb
                    ));
                }

                return Ok(new
                {
                    code = 0,
                    message = "Thêm Thiết Bị Vào Thành Công",
                    deviceGuid,
                    deviceName,
                    added = resp.data
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ErrorId}] AddDevice internal error", errorId);

                return StatusCode(500, BuildError(
                    errorId: errorId,
                    message: "Lỗi Máy Chủ",
                    ex: ex
                ));
            }
        }

        // =========================
        // SESSIONS VIEW
        // =========================
        public class AccountInfoVm
        {
            public string Username { get; set; } = "";
            public string? FullName { get; set; }
        }

        [HttpGet("Sessions")]
        public async Task<IActionResult> Sessions(string? q, CancellationToken ct)
        {
            q = (q ?? "").Trim();
            var hasQ = !string.IsNullOrWhiteSpace(q);

            var fb = Builders<LoginSession>.Filter;
            var filter = fb.Empty;

            List<string> matchedAccountIds = new();
            if (hasQ)
            {
                var qLower = q.ToLowerInvariant();
                matchedAccountIds = await _db.Accounts
                    .Find(a => a.Username != null && a.Username.ToLower().Contains(qLower))
                    .Project(a => a.Id)
                    .Limit(200)
                    .ToListAsync(ct);

                var ors = new List<FilterDefinition<LoginSession>>
                {
                    fb.Regex(x => x.SessionId, new BsonRegularExpression(q, "i")),
                    fb.Regex(x => x.Ip, new BsonRegularExpression(q, "i")),
                    fb.Regex(x => x.UserAgent, new BsonRegularExpression(q, "i")),
                    fb.Regex(x => x.RevokedReason, new BsonRegularExpression(q, "i")),
                    fb.Regex(x => x.AccountId, new BsonRegularExpression(q, "i")),
                };

                if (matchedAccountIds.Count > 0)
                    ors.Insert(0, fb.In(x => x.AccountId, matchedAccountIds));

                filter = fb.Or(ors);
            }

            var sessions = await _db.LoginSessions
                .Find(filter)
                .SortByDescending(x => x.LoginAtUtc)
                .Limit(500)
                .ToListAsync(ct);

            var accountIds = sessions
                .Select(s => s.AccountId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();

            var accounts = await _db.Accounts
                .Find(a => accountIds.Contains(a.Id))
                .Project(a => new { a.Id, a.Username })
                .ToListAsync(ct);

            var map = accounts.ToDictionary(
                x => x.Id,
                x => new AccountInfoVm { Username = x.Username, FullName = null }
            );

            ViewBag.AccountMap = map;
            ViewBag.Q = q;

            return View(sessions);
        }

        // =========================
        // VIEW MODEL
        // =========================
        public class CreateUserViewModel
        {
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
            public RoleViewModel Role { get; set; } = RoleViewModel.User;
            public string? Phone { get; set; }
        }

        // =========================
        // Helpers: error shaping
        // =========================
        private object BuildError(
            string errorId,
            string message,
            Exception ex,
            string? upstreamBody = null,
            int? upstreamStatus = null)
        {
            if (_env.IsDevelopment())
            {
                return new
                {
                    errorId,
                    message,
                    detail = ex.ToString(),
                    upstreamStatus,
                    upstreamBody
                };
            }

            return new { errorId, message };
        }

        private static string? TryExtractUpstreamBody(Exception ex)
        {
            var msg = ex.Message ?? "";
            var idx = msg.IndexOf("Body:", StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? null : msg[(idx + 5)..].Trim();
        }

        private static int? TryExtractUpstreamStatus(Exception ex)
        {
            var msg = ex.Message ?? "";
            var token = "HTTP ";
            var i = msg.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;

            i += token.Length;
            var j = i;
            while (j < msg.Length && char.IsDigit(msg[j])) j++;

            return j == i
                ? null
                : int.TryParse(msg[i..j], out var code) ? code : null;
        }

        // =========================
        // ✅ SMS ADMIN ENDPOINTS
        // =========================

        [HttpPost("Sms/Stop")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsStop([FromForm] string username, CancellationToken ct)
        {
            var ok = await _accounts.AdminStopSmsAsync(username, ct);
            return Json(new { ok, reason = ok ? null : "Stop failed" });
        }

        [HttpPost("Sms/On")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsOn([FromForm] string username, CancellationToken ct)
        {
            var ok = await _accounts.AdminTurnOnSmsAsync(username, ct);
            return Json(new { ok, reason = ok ? null : "On failed" });
        }

        [HttpPost("Sms/AdjustDays")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsAdjustDays([FromForm] string username, [FromForm] int addDays, CancellationToken ct)
        {
            var (ok, reason, newExpiresUtc) = await _accounts.AdminAdjustSmsDaysAsync(username, addDays, ct);
            return Json(new { ok, reason, newExpiresUtc });
        }

        // ✅ endpoint JS modal "Set expiry" đang gọi
        [HttpPost("Sms/SetExpiry")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsSetExpiry([FromForm] string username, [FromForm] string expiryDate, CancellationToken ct)
        {
            var (ok, reason, newExpiresUtc) = await _accounts.AdminSetSmsExpiryDateAsync(username, expiryDate, ct);
            return Json(new { ok, reason, newExpiresUtc });
        }

        // ✅ LIST: load ALL accounts để bật cho user mới tạo
        [HttpGet("SmsSubscriptions/List")]
        public async Task<IActionResult> SmsSubscriptionsList(CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var list = await _accounts.GetAllAsync(); // ✅ quan trọng: ra hết user

            var data = list.Select(u =>
            {
                var exp = u.SmsSubExpiresAtUtc;

                var status =
                    (u.SmsSubActive && exp.HasValue && exp.Value > now) ? "ACTIVE"
                  : (exp.HasValue && exp.Value <= now) ? "EXPIRED"
                  : "OFF";

                return new
                {
                    username = u.Username,
                    userId = u.Id,
                    phone = u.Phone,
                    phoneVerified = u.PhoneVerified,

                    smsSelfRegisterAllowed = u.SmsSelfRegisterAllowed,
                    smsAlertEnabled = u.SmsAlertEnabled,

                    smsSubActive = u.SmsSubActive,
                    smsSubStartedAtUtc = u.SmsSubStartedAtUtc,
                    smsSubExpiresAtUtc = u.SmsSubExpiresAtUtc,

                    status
                };
            });

            return Json(new { ok = true, data });
        }
        [HttpPost("Sms/SetSelfRegisterAllowed")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsSetSelfRegisterAllowed(
     [FromForm] string username,
     [FromForm] bool allowed,
     CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Json(new { ok = false, reason = "username required" });

            username = username.Trim().ToLowerInvariant();

            var filter = Builders<AccountViewModel>.Filter.Eq(x => x.Username, username);

            var update = allowed
                ? Builders<AccountViewModel>.Update
                    .Set(x => x.SmsSelfRegisterAllowed, true)
                    .Set(x => x.SmsAlertEnabled, true)
                : Builders<AccountViewModel>.Update
                    .Set(x => x.SmsSelfRegisterAllowed, false)
                    .Set(x => x.SmsAlertEnabled, false)
                    .Set(x => x.SmsSubActive, false);

            var res = await _db.Accounts.UpdateOneAsync(
                filter,
                update,
                cancellationToken: ct
            );

            return Json(new
            {
                ok = res.MatchedCount > 0,
                reason = res.MatchedCount > 0 ? null : "User not found"
            });
        }
        // (optional) nếu không dùng nữa thì xoá
        [HttpPost("Sms/Start30")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SmsStart30([FromForm] string username, CancellationToken ct)
        {
            var ok = await _accounts.StartSms30DaysAsync(username, ct);
            return Json(new { ok, reason = ok ? null : "Start30 failed" });
        }
        [HttpPost("/Admin/Sms/Clear")]
        [Authorize(Roles = "Admin")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Clear(string username, CancellationToken ct)
        {
            await _accounts.ClearSmsSubscriptionAsync(username, ct);
            return Json(new { ok = true });
        }
        [HttpGet("/api/admin/users")]
        public async Task<IActionResult> ApiUsers()
        {
            return await GetUsers();
        }

    }
}