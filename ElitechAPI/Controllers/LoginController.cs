using Elitech.Services;
using Elitech.Models;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

using System.Security.Claims;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Elitech.Controllers
{
    public class LoginController : Controller
    {
        private readonly AccountService _accounts;
        private readonly LoginSessionService _sessions;

        public LoginController(AccountService accounts, LoginSessionService sessions)
        {
            _accounts = accounts;
            _sessions = sessions;
        }

      
        [HttpGet, AllowAnonymous]
        public IActionResult Index(string? returnUrl = null)
        {
            if (User?.Identity?.IsAuthenticated == true)
                return RedirectToAction("Router", "Home", new { returnUrl });

            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        public class LoginViewModel
        {
            public string Username { get; set; } = "";
            public string Password { get; set; } = "";
        }

        
        [HttpPost, ValidateAntiForgeryToken, AllowAnonymous]
        public async Task<IActionResult> Index(LoginViewModel model, string? returnUrl = null)
        {
            // 1) Validate input
            if (string.IsNullOrWhiteSpace(model.Username) ||
                string.IsNullOrWhiteSpace(model.Password))
            {
                ModelState.AddModelError("", "Vui lòng nhập đầy đủ thông tin.");
                return View(model);
            }

            // 2) Validate password
            if (!await _accounts.ValidatePasswordAsync(model.Username, model.Password))
            {
                ModelState.AddModelError("", "Sai tài khoản hoặc mật khẩu.");
                return View(model);
            }

            // 3) Load account
            var acc = await _accounts.GetByUsernameAsync(model.Username);
            if (acc is null || !acc.IsActive)
            {
                ModelState.AddModelError("", "Tài khoản không hoạt động.");
                return View(model);
            }

            //  CREATE SESSION (MULTI DEVICE)
            var session = await _sessions.CreateSessionAsync(
                acc.Id,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                Request.Headers.UserAgent.ToString()
            );

            var sessionId = session.SessionId;

            var role = acc.Role.ToString();

            var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, acc.Id),
                    new Claim(ClaimTypes.Name, acc.Username),
                    new Claim(ClaimTypes.Role, role), // ✅ đúng
                    new Claim("sid", sessionId),
                };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
                }
            );

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                if (returnUrl.Equals("/api/elitech/user", StringComparison.OrdinalIgnoreCase))
                    return Redirect("/user");

                return Redirect(returnUrl);
            }

            return RedirectToAction("Router", "Home");
        }

        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            var sid = User?.Claims?.FirstOrDefault(x => x.Type == "sid")?.Value;

            if (!string.IsNullOrWhiteSpace(sid))
            {
                await _sessions.LogoutBySessionIdAsync(sid);
            }

            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Index", "Home");
        }

        // =========================
        // API: SESSION STATUS
        // =========================
        [HttpGet("/api/session/status")]
        [Authorize]
        public async Task<IActionResult> SessionStatus(CancellationToken ct)
        {
            var sid = User?.Claims?.FirstOrDefault(x => x.Type == "sid")?.Value;
            if (string.IsNullOrWhiteSpace(sid))
                return Unauthorized();

            var (_, status) = await _sessions.GetStatusAsync(sid, ct);

            return Ok(new
            {
                status.ServerNowUtc,
                status.ExpiresAtUtc,
                status.RemainingSeconds,
                status.ShouldWarn,
                status.ShouldLogout,
                status.Revoked,
                status.RevokedReason
            });
        }

        // =========================
        // API: KEEP ALIVE
        // =========================
        [HttpPost("/api/session/keepalive")]
        [Authorize]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> KeepAlive(CancellationToken ct)
        {
            var sid = User?.Claims?.FirstOrDefault(x => x.Type == "sid")?.Value;
            if (string.IsNullOrWhiteSpace(sid))
                return Unauthorized();

            await _sessions.TouchAsync(sid, ct);
            return Ok(new { code = 0 });
        }

        // =========================
        // API: LIST SESSIONS
        // =========================
        [HttpGet("/api/session/list")]
        [Authorize]
        public async Task<IActionResult> ListSessions(CancellationToken ct)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var list = await _sessions.GetSessionsByUserAsync(userId!, ct);
            var currentSid = User.FindFirst("sid")?.Value;

            return Ok(list.Select(x => new
            {
                x.SessionId,
                x.Ip,
                x.UserAgent,
                x.LoginAtUtc,
                x.LastActivityAtUtc,
                x.IsActive,
                isCurrent = x.SessionId == currentSid
            }));
        }

        // =========================
        // API: REVOKE SESSION
        // =========================
        [HttpPost("/api/session/revoke")]
        [Authorize]
        public async Task<IActionResult> Revoke(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return BadRequest();

            await _sessions.RevokeBySessionIdAsync(sessionId, "Manual logout");
            return Ok(new { ok = true });
        }

        [AllowAnonymous]
        public IActionResult Denied() => Content("Bạn không có quyền truy cập.");

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Register()
        {
            return View();
        }
        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Register(string username, string email, string password, string confirmPassword)
        {
            username = (username ?? "").Trim().ToLowerInvariant(); // 👈 THÊM DÒNG NÀY

            if (password != confirmPassword)
            {
                ModelState.AddModelError("", "Mật khẩu không khớp");
                ViewBag.IsRegister = true;
                return View("Index");
            }

            var exist = await _accounts.GetByUsernameAsync(username);
            if (exist != null)
            {
                ModelState.AddModelError("", "Username đã tồn tại");
                ViewBag.IsRegister = true;
                return View("Index");
            }

            var acc = await _accounts.CreateAsync(username, password, RoleViewModel.User);

            await _accounts.UpdateProfileAsync(username, null, email, null, null);

            TempData["RegisterSuccess"] = "Đăng ký thành công!";

            return RedirectToAction("Index");
        }
    }
}