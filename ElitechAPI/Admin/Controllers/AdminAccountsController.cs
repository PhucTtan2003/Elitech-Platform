using Elitech.Models;
using Elitech.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;

namespace Elitech.Admin.Controllers
{
    // File: Admin/Controllers/AdminAccountsController.cs
    [ApiController]
    [Authorize(Roles = "Admin")]
    [Route("api/admin/accounts")]
    public class AdminAccountsController : ControllerBase
    {
        private readonly AccountService _accountService;

        public AdminAccountsController(AccountService accountService)
        {
            _accountService = accountService;
        }

        // GET: /api/admin/accounts
        // Optional: /api/admin/accounts?role=admin|user
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? role = null)
        {
            try
            {
                List<AccountViewModel> items;

                if (!string.IsNullOrWhiteSpace(role))
                {
                    // hỗ trợ truyền "admin"/"user"
                    items = await _accountService.GetByRoleAsync(role);
                }
                else
                {
                    items = await _accountService.GetAllAsync();
                }

                // Trả về đúng fields JS đang dùng:
                // username, role, isActive, createdAtUtc
                var data = items.Select(x => new
                {
                    username = x.Username,
                    role = x.Role,             // RoleViewModel
                    isActive = x.IsActive,
                    phone = x.Phone,
                    createdAtUtc = x.CreatedAtUtc
                }).ToList();

                return Ok(new
                {
                    code = 0,
                    message = "OK",
                    data
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    code = 500,
                    message = "Server error",
                    detail = ex.Message
                });
            }
        }

        // (OPTIONAL) PATCH: /api/admin/accounts/{username}/active
        // body: { "isActive": true/false }
        public class SetActiveReq
        {
            public bool IsActive { get; set; }
        }

        [HttpPatch("{username}/active")]
        public async Task<IActionResult> SetActive([FromRoute] string username, [FromBody] SetActiveReq req)
        {
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest(new { code = 400, message = "username is required" });

            try
            {
                var ok = await _accountService.SetActiveAsync(username, req.IsActive);
                if (!ok)
                    return NotFound(new { code = 404, message = "Account not found or not updated" });

                return Ok(new { code = 0, message = "OK", data = new { username, isActive = req.IsActive } });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { code = 500, message = "Server error", detail = ex.Message });
            }
        }

        // =========================================================
        // ====== (THÊM TỪ ĐOẠN 1) THÊM / SỬA / XÓA / RESET PW ======
        // =========================================================

        // =========================
        // POST: /api/admin/accounts
        // Body: { username, password, role(1/2), isActive, fullName, email, phone }
        // =========================
        public class CreateAccountReq
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int Role { get; set; } = 3;      // 1=Admin, 2=User
    public bool IsActive { get; set; } = true;

    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

        private static RoleViewModel ParseRole(int role)
        {
            return role switch
            {
                1 => RoleViewModel.Admin,
                2 => RoleViewModel.User,
                3 => RoleViewModel.Staff, 
                _ => RoleViewModel.User
            };
        }

        [HttpPost]
public async Task<IActionResult> Create([FromBody] CreateAccountReq req)
{
    if (req is null) return BadRequest(new { code = 400, message = "body is required" });

    req.Username = (req.Username ?? "").Trim();
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return BadRequest(new { code = 400, message = "username & password are required" });

    try
    {
        var existing = await _accountService.GetByUsernameAsync(req.Username);
        if (existing != null)
            return Conflict(new { code = 409, message = "Username already exists" });

        await _accountService.CreateAsync(req.Username, req.Password, ParseRole(req.Role));

        await _accountService.UpdateProfileAsync(
            username: req.Username,
            fullName: req.FullName,
            email: req.Email,
            phone: req.Phone,
            avatarUrl: null
        );

        if (!req.IsActive)
            await _accountService.SetActiveAsync(req.Username, false);

        return Ok(new { code = 0, message = "Created", data = new { username = req.Username } });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { code = 500, message = "Server error", detail = ex.Message });
    }
}

// =========================
// PUT: /api/admin/accounts/{username}
// Body: { role(1/2), isActive, fullName, email, phone }
// =========================
public class UpdateAccountReq
{
    public int? Role { get; set; }
    public bool? IsActive { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

[HttpPut("{username}")]
public async Task<IActionResult> Update([FromRoute] string username, [FromBody] UpdateAccountReq req)
{
    username = (username ?? "").Trim();
    if (string.IsNullOrWhiteSpace(username))
        return BadRequest(new { code = 400, message = "username is required" });
    if (req is null)
        return BadRequest(new { code = 400, message = "body is required" });

    try
    {
        var acc = await _accountService.GetByUsernameAsync(username);
        if (acc is null)
            return NotFound(new { code = 404, message = "Account not found" });

        if (req.Role.HasValue)
            await _accountService.UpdateRoleAsync(username, ParseRole(req.Role.Value));

        if (req.IsActive.HasValue)
            await _accountService.SetActiveAsync(username, req.IsActive.Value);

        if (req.FullName != null || req.Email != null || req.Phone != null)
        {
            await _accountService.UpdateProfileAsync(
                username: username,
                fullName: req.FullName,
                email: req.Email,
                phone: req.Phone,
                avatarUrl: null
            );
        }

        return Ok(new { code = 0, message = "Updated" });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { code = 500, message = "Server error", detail = ex.Message });
    }
}

// =========================
// POST: /api/admin/accounts/{username}/reset-password
// Body: { newPassword }
// =========================
public class ResetPasswordReq
{
    public string NewPassword { get; set; } = "";
}

[HttpPost("{username}/reset-password")]
public async Task<IActionResult> ResetPassword([FromRoute] string username, [FromBody] ResetPasswordReq req)
{
    username = (username ?? "").Trim();
    if (string.IsNullOrWhiteSpace(username))
        return BadRequest(new { code = 400, message = "username is required" });

    var newPw = (req?.NewPassword ?? "").Trim();
    if (string.IsNullOrWhiteSpace(newPw))
        return BadRequest(new { code = 400, message = "newPassword is required" });

    try
    {
        var ok = await _accountService.AdminResetPasswordAsync(username, newPw);
        if (!ok) return BadRequest(new { code = 400, message = "Reset password failed" });
        return Ok(new { code = 0, message = "OK" });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { code = 500, message = "Server error", detail = ex.Message });
    }
}

// =========================
// DELETE: /api/admin/accounts/{idOrUsername}
// (chấp nhận username hoặc ObjectId)
// =========================
[HttpDelete("{idOrUsername}")]
public async Task<IActionResult> Delete([FromRoute] string idOrUsername)
{
    idOrUsername = (idOrUsername ?? "").Trim();
    if (string.IsNullOrWhiteSpace(idOrUsername))
        return BadRequest(new { code = 400, message = "Missing id/username" });

    if (idOrUsername.Equals("admin", StringComparison.OrdinalIgnoreCase))
        return BadRequest(new { code = 400, message = "Cannot delete root admin" });

    try
    {
        bool ok;
        if (ObjectId.TryParse(idOrUsername, out var oid))
            ok = await _accountService.DeleteByIdAsync(oid.ToString());
        else
            ok = await _accountService.DeleteByUsernameAsync(idOrUsername);

        if (!ok)
            return BadRequest(new { code = 400, message = "Delete failed (not found or blocked)" });

        return Ok(new { code = 0, message = "Deleted" });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { code = 500, message = "Server error", detail = ex.Message });
    }
}
    }
}
