using Elitech.Data;
using Elitech.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Elitech.Services
{
    public class AccountService
    {
        private readonly MongoContext _ctx;

        public AccountService(MongoContext ctx)
        {
            _ctx = ctx;
        }

        // =========================
        // BASIC
        // =========================
        public Task<AccountViewModel> GetByUsernameAsync(string username)
        {
            username = (username ?? "").Trim().ToLowerInvariant();
            return _ctx.Accounts.Find(x => x.Username == username).FirstOrDefaultAsync();
        }

        public async Task<bool> ValidatePasswordAsync(string username, string password)
        {
            var acc = await GetByUsernameAsync(username);
            if (acc is null || !acc.IsActive) return false;
            return BCrypt.Net.BCrypt.Verify(password, acc.PasswordHash);
        }

        public async Task<AccountViewModel> CreateAsync(string username, string password, RoleViewModel role)
        {
            username = (username ?? "").Trim().ToLowerInvariant();
            var hash = BCrypt.Net.BCrypt.HashPassword(password);
            
            var acc = new AccountViewModel
            {
                Username = username,
                PasswordHash = hash,
                Role = role,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow,

                FullName = null,
                Email = null,
                Phone = null,

                // Default: khóa self-register cho tới khi admin bật
                SmsSelfRegisterAllowed = false,

                // optional: đồng bộ luôn trạng thái sms
                SmsSubActive = false,
                SmsAlertEnabled = false,
                PhoneVerified = false
            };


            await _ctx.Accounts.InsertOneAsync(acc);
            return acc;
        }

        public async Task SeedAsync()
        {
            var idx = new CreateIndexModel<AccountViewModel>(
                Builders<AccountViewModel>.IndexKeys.Ascending(x => x.Username),
                new CreateIndexOptions { Unique = true });

            await _ctx.Accounts.Indexes.CreateOneAsync(idx);

            if (await GetByUsernameAsync("admin") is null)
                await CreateAsync("admin", "Admin@123", RoleViewModel.Admin);

            if (await GetByUsernameAsync("user1") is null)
                await CreateAsync("user1", "User@123", RoleViewModel.User);
        }

        // =========================
        // DÙNG CHO BẢNG ACCOUNTS
        // =========================
        public async Task<List<AccountViewModel>> GetAllAsync()
        {
            return await _ctx.Accounts
                .Find(Builders<AccountViewModel>.Filter.Empty)
                .SortByDescending(x => x.CreatedAtUtc)
                .ToListAsync();
        }

        // Chuẩn nhất: filter theo RoleViewModel
        public async Task<List<AccountViewModel>> GetByRoleAsync(RoleViewModel role)
        {
            return await _ctx.Accounts.Find(x => x.Role == role).ToListAsync();
        }

        // Optional: nếu controller truyền string ("User"/"Admin") thì vẫn dùng được
        public Task<List<AccountViewModel>> GetByRoleAsync(string role)
        {
            var r = (role ?? "").Trim().ToLowerInvariant();
            var parsed = r switch
            {
                "admin" => RoleViewModel.Admin,
                "user" => RoleViewModel.User,
                _ => (RoleViewModel?)null
            };

            if (parsed is null)
                return Task.FromResult(new List<AccountViewModel>());

            return GetByRoleAsync(parsed.Value);
        }

        // =========================
        // (Optional) BẬT/TẮT ACCOUNT
        // =========================
        public async Task<bool> SetActiveAsync(string username, bool isActive)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;

            var update = Builders<AccountViewModel>.Update.Set(x => x.IsActive, isActive);
            var res = await _ctx.Accounts.UpdateOneAsync(x => x.Username == username, update);

            return res.ModifiedCount > 0;
        }

        // =========================================================
        // PROFILE / SETTINGS (MongoContext style)
        // =========================================================

        //  Lấy thông tin profile (theo username)
        public Task<AccountViewModel> GetProfileAsync(string username)
            => _ctx.Accounts.Find(x => x.Username == username).FirstOrDefaultAsync();

        //  Update thông tin cá nhân
        public async Task<bool> UpdateProfileAsync(
    string username,
    string? fullName,
    string? email,
    string? phone,
    string? avatarUrl)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            //  xử lý tối đa 5 số điện thoại
            var phones = (phone ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .Take(5)
                .ToList();

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.FullName, Norm(fullName))
                .Set(x => x.Email, Norm(email))
                .Set(x => x.Phones, phones)
                .Set(x => x.Phone, phones.FirstOrDefault()); // số chính

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update);

            return res.ModifiedCount > 0;
        }


        //  Đổi mật khẩu (Settings)
        // - kiểm tra mật khẩu hiện tại
        // - hash mật khẩu mới
        public async Task<bool> ChangePasswordAsync(
            string username,
            string currentPassword,
            string newPassword)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;
            if (string.IsNullOrWhiteSpace(currentPassword)) return false;
            if (string.IsNullOrWhiteSpace(newPassword)) return false;

            var acc = await GetByUsernameAsync(username);
            if (acc is null || !acc.IsActive) return false;

            if (!BCrypt.Net.BCrypt.Verify(currentPassword, acc.PasswordHash))
                return false;

            var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.PasswordHash, newHash);

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update);

            return res.ModifiedCount > 0;
        }


        public async Task<string?> GetPhoneByUserIdAsync(string userId)
        {
            userId = (userId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(userId)) return null;

            // 1) thử coi userId là username
            var accByUsername = await GetByUsernameAsync(userId);
            if (accByUsername != null)
            {
                var p1 = (accByUsername.Phone ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(p1)) return p1;
            }

            // 2) thử coi userId là Id string trong Accounts
            var accById = await _ctx.Accounts.Find(x => x.Id == userId).FirstOrDefaultAsync();
            var p2 = (accById?.Phone ?? "").Trim();
            return string.IsNullOrWhiteSpace(p2) ? null : p2;
        }

        public Task SetPhoneVerifyPendingAsync(
      string username,
      string normalizedPhone,
      string codeHash,
      DateTime expiresAtUtc,
      DateTime sentAtUtc,
      CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username))
                return Task.CompletedTask;

            var filter = Builders<AccountViewModel>.Filter.Eq(x => x.Username, username);

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.Phone, normalizedPhone)
                .Set(x => x.Phones, new List<string> { normalizedPhone })
                .Set(x => x.PhoneVerified, false)
                .Set(x => x.PhoneVerifiedAtUtc, (DateTime?)null)
                .Set(x => x.PhoneVerifyCodeHash, codeHash)
                .Set(x => x.PhoneVerifyExpiresAtUtc, expiresAtUtc)
                .Set(x => x.PhoneVerifyLastSentAtUtc, sentAtUtc)
                .Set(x => x.SmsAlertEnabled, false);

            return _ctx.Accounts.UpdateOneAsync(filter, update, cancellationToken: ct);
        }

        public Task MarkPhoneVerifiedAsync(string username, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username))
                return Task.CompletedTask;

            var now = DateTime.UtcNow;
            var filter = Builders<AccountViewModel>.Filter.Eq(x => x.Username, username);

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.PhoneVerified, true)
                .Set(x => x.PhoneVerifiedAtUtc, now)
                .Set(x => x.SmsAlertEnabled, true) // auto bật

                // ✅ bật dịch vụ 30 ngày
                .Set(x => x.SmsSubActive, true)
                .Set(x => x.SmsSubStartedAtUtc, now)
                .Set(x => x.SmsSubExpiresAtUtc, now.AddDays(30))
                // ✅ bật nhận cảnh báo (nếu bạn muốn auto bật)
                .Set(x => x.SmsAlertEnabled, true)

                .Set(x => x.PhoneVerifyCodeHash, (string?)null)
                .Set(x => x.PhoneVerifyExpiresAtUtc, (DateTime?)null);

            return _ctx.Accounts.UpdateOneAsync(filter, update, cancellationToken: ct);
        }
        public async Task<long> DisableExpiredSmsSubscriptionsAsync(CancellationToken ct)
        {
            var now = DateTime.UtcNow;

            var filter = Builders<AccountViewModel>.Filter.And(
                Builders<AccountViewModel>.Filter.Eq(x => x.SmsSubActive, true),
                Builders<AccountViewModel>.Filter.Lte(x => x.SmsSubExpiresAtUtc, now)
            );

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubActive, false)
                .Set(x => x.SmsAlertEnabled, false);

            var res = await _ctx.Accounts.UpdateManyAsync(filter, update, cancellationToken: ct);
            return res.ModifiedCount;
        }
        public async Task<bool> AdminDisableSmsAsync(string username, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var filter = Builders<AccountViewModel>.Filter.Eq(x => x.Username, username);

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubActive, false)
                .Set(x => x.SmsAlertEnabled, false)
                .Set(x => x.SmsSelfRegisterAllowed, false);
            var res = await _ctx.Accounts.UpdateOneAsync(
                filter,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0;
        }

        public async Task<bool> AdminExtendSmsAsync(string username, int addDays, CancellationToken ct)
        {
            addDays = Math.Clamp(addDays, 1, 365);

            var acc = await GetByUsernameAsync(username);
            if (acc == null) return false;

            var baseTime = acc.SmsSubExpiresAtUtc.HasValue && acc.SmsSubExpiresAtUtc.Value > DateTime.UtcNow
                ? acc.SmsSubExpiresAtUtc.Value
                : DateTime.UtcNow;

            var newExp = baseTime.AddDays(addDays);

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubActive, true)
                .Set(x => x.SmsSubExpiresAtUtc, newExp);

            var res = await _ctx.Accounts.UpdateOneAsync(x => x.Username == username, update, cancellationToken: ct);
            return res.ModifiedCount > 0;
        }
        public async Task<bool> AdminStopSmsAsync(string username, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubActive, false)
                .Set(x => x.SmsAlertEnabled, false)
                .Set(x => x.SmsSelfRegisterAllowed, false); // ✅ QUAN TRỌNG: stop => nút user mờ

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0;
        }

        // ✅ cộng/trừ ngày (addDays có thể âm)
        // - nếu đang active và chưa hết hạn => cộng/trừ trên Expires hiện tại
        // - nếu đã hết hạn / chưa có expires => base = now
        public async Task<(bool ok, string? reason, DateTime? newExpiresUtc)> AdminAdjustSmsDaysAsync(
       string username,
       int addDays,
       CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username))
                return (false, "username required", null);

            if (addDays == 0)
                return (false, "addDays must not be 0", null);

            // giới hạn tránh add bậy
            addDays = Math.Clamp(addDays, -365, 365);

            var acc = await GetByUsernameAsync(username);
            if (acc == null) return (false, "User not found", null);

            var now = DateTime.UtcNow;

            // base = expires hiện tại nếu còn hạn, không thì base = now
            var baseTime =
                acc.SmsSubExpiresAtUtc.HasValue && acc.SmsSubExpiresAtUtc.Value > now
                    ? acc.SmsSubExpiresAtUtc.Value
                    : now;

            var newExp = baseTime.AddDays(addDays);

            // nếu admin trừ ngày làm newExp <= now => coi như hết hạn và tắt
            var shouldBeActive = newExp > now;

            var update = Builders<AccountViewModel>.Update
    .Set(x => x.SmsSubExpiresAtUtc, newExp)
    .Set(x => x.SmsSubActive, shouldBeActive)
    .Set(x => x.SmsSelfRegisterAllowed, shouldBeActive)
    .Set(x => x.SmsAlertEnabled, shouldBeActive);

            if (!shouldBeActive)
            {
                // hết hạn / bị giảm quá đà => tắt nhận sms
                update = update.Set(x => x.SmsAlertEnabled, false);
            }
            else
            {
                // nếu trước đó chưa có startedAt => set
                if (!acc.SmsSubStartedAtUtc.HasValue)
                    update = update.Set(x => x.SmsSubStartedAtUtc, now);
            }

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0
                ? (true, null, newExp)
                : (false, "No changes", acc.SmsSubExpiresAtUtc);
        }


        //  set gói 30 ngày (dùng khi user verify xong hoặc admin kích hoạt lại)
        public async Task<bool> StartSms30DaysAsync(string username, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var now = DateTime.UtcNow;

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubActive, true)
                .Set(x => x.SmsSubStartedAtUtc, now)
                .Set(x => x.SmsSubExpiresAtUtc, now.AddDays(30))
                .Set(x => x.SmsSelfRegisterAllowed, true)
                .Set(x => x.SmsAlertEnabled, true);

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0;
        }

        public async Task<List<AccountViewModel>> GetSmsSubscribersAsync(CancellationToken ct)
        {
            // lấy tất cả acc có phone verified hoặc đã từng bật sms service
            var fb = Builders<AccountViewModel>.Filter;

            var filter = fb.Or(
                fb.Eq(x => x.SmsSubActive, true),
                fb.Ne(x => x.SmsSubExpiresAtUtc, null),
                fb.Eq(x => x.PhoneVerified, true)
            );

            return await _ctx.Accounts
                .Find(filter)
                .SortByDescending(x => x.SmsSubActive)
                .ThenByDescending(x => x.SmsSubExpiresAtUtc)
                .ThenBy(x => x.Username)
                .ToListAsync(ct);
        }
        public async Task<bool> AdminTurnOnSmsAsync(string username, CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var acc = await GetByUsernameAsync(username);
            if (acc == null) return false;

            var now = DateTime.UtcNow;

            var exp = (acc.SmsSubExpiresAtUtc.HasValue && acc.SmsSubExpiresAtUtc.Value > now)
                ? acc.SmsSubExpiresAtUtc.Value
                : now.AddDays(30);

            var update = Builders<AccountViewModel>.Update
    .Set(x => x.SmsSubActive, true)
    .Set(x => x.SmsSubStartedAtUtc, now)
    .Set(x => x.SmsSubExpiresAtUtc, now.AddDays(30))
    .Set(x => x.SmsSelfRegisterAllowed, true)
    .Set(x => x.SmsAlertEnabled, true);

            //  Nếu đã verify phone rồi thì bật nhận cảnh báo luôn
            if (acc.PhoneVerified && !string.IsNullOrWhiteSpace(acc.Phone))
                update = update.Set(x => x.SmsAlertEnabled, true);

            if (!acc.SmsSubStartedAtUtc.HasValue)
                update = update.Set(x => x.SmsSubStartedAtUtc, now);

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0;
        }
 

        public async Task<(bool ok, string? reason, DateTime? newExpiresUtc)> AdminSetSmsExpiryDateAsync(
    string username,
    string expiryDate,
    CancellationToken ct)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username))
                return (false, "username required", null);

            expiryDate = (expiryDate ?? "").Trim();
            if (string.IsNullOrWhiteSpace(expiryDate))
                return (false, "expiryDate required", null);

            if (!DateTime.TryParseExact(expiryDate, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var day))
            {
                return (false, "Invalid date format (yyyy-MM-dd)", null);
            }

            var acc = await GetByUsernameAsync(username);
            if (acc == null) return (false, "User not found", null);

            var now = DateTime.UtcNow;

            // set hết ngày (UTC)
            var newExp = new DateTime(day.Year, day.Month, day.Day, 23, 59, 59, DateTimeKind.Utc);

            var shouldBeActive = newExp > now;

            var update = Builders<AccountViewModel>.Update
                .Set(x => x.SmsSubExpiresAtUtc, newExp)
                .Set(x => x.SmsSubActive, shouldBeActive)
                .Set(x => x.SmsSelfRegisterAllowed, shouldBeActive);

            if (!shouldBeActive)
                update = update.Set(x => x.SmsAlertEnabled, false);

            if (shouldBeActive && !acc.SmsSubStartedAtUtc.HasValue)
                update = update.Set(x => x.SmsSubStartedAtUtc, now);

            var res = await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct);

            return res.ModifiedCount > 0
                ? (true, null, newExp)
                : (false, "No changes", acc.SmsSubExpiresAtUtc);
        }
        public async Task<AccountViewModel?> GetByIdAsync(string id, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;

            // validate ObjectId format để tránh query rác
            if (!ObjectId.TryParse(id, out _)) return null;

            return await _ctx.Accounts
                .Find(x => x.Id == id)
                .FirstOrDefaultAsync(ct);
        }

        public async Task<AccountViewModel?> GetByIdOrUsernameAsync(string idOrUsername, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(idOrUsername)) return null;

            // 1) nếu là ObjectId string => tìm theo Id
            if (ObjectId.TryParse(idOrUsername, out _))
            {
                var byId = await GetByIdAsync(idOrUsername, ct);
                if (byId != null) return byId;
            }

            // 2) fallback theo username
            return await GetByUsernameAsync(idOrUsername);
        }
        // (đoạn 1) DELETE by id (ObjectId string)
        public async Task<bool> DeleteByIdAsync(string id)
        {
            id = (id ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) return false;

            if (!ObjectId.TryParse(id, out _)) return false;

            // AccountViewModel.Id là string (BsonRepresentation ObjectId)
            var res = await _ctx.Accounts.DeleteOneAsync(x => x.Id == id);
            return res.DeletedCount > 0;
        }

        // (đoạn 1) DELETE by username
        public async Task<bool> DeleteByUsernameAsync(string username)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var res = await _ctx.Accounts.DeleteOneAsync(x => x.Username == username);
            return res.DeletedCount > 0;
        }

        // (đoạn 1) ROLE update
        public async Task<bool> UpdateRoleAsync(string username, RoleViewModel role)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;

            var update = Builders<AccountViewModel>.Update.Set(x => x.Role, role);
            var res = await _ctx.Accounts.UpdateOneAsync(x => x.Username == username, update);
            return res.ModifiedCount > 0;
        }

        // (đoạn 1) ADMIN reset password
        public async Task<bool> AdminResetPasswordAsync(string username, string newPassword)
        {
            username = (username ?? "").Trim();
            if (string.IsNullOrWhiteSpace(username)) return false;
            if (string.IsNullOrWhiteSpace(newPassword)) return false;

            var newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

            var update = Builders<AccountViewModel>.Update.Set(x => x.PasswordHash, newHash);
            var res = await _ctx.Accounts.UpdateOneAsync(x => x.Username == username, update);
            return res.ModifiedCount > 0;
        }
        public async Task ClearSmsSubscriptionAsync(string username, CancellationToken ct)
        {
            var update = Builders<AccountViewModel>.Update
                .Set("phone", (string?)null)
                .Set("phones", new List<string>())
                .Set("phoneVerified", false)
                .Set("phoneVerifiedAtUtc", (DateTime?)null)
                .Set("phoneVerifyCodeHash", (string?)null)
                .Set("phoneVerifyExpiresAtUtc", (DateTime?)null)
                .Set("phoneVerifyLastSentAtUtc", (DateTime?)null)

                // SMS
                .Set("smsAlertEnabled", false)
                .Set("smsSubActive", false)
                .Set("smsSubStartedAtUtc", (DateTime?)null)
                .Set("smsSubExpiresAtUtc", (DateTime?)null);

            await _ctx.Accounts.UpdateOneAsync(
                x => x.Username == username,
                update,
                cancellationToken: ct
            );
        }
       
    }
}