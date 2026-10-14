using Elitech.Data;
using Elitech.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace Elitech.Services
{
    public class LoginSessionService
    {
        private readonly MongoContext _ctx;
        private readonly IOptionsMonitor<SessionPolicyOptions> _opt;

        public LoginSessionService(MongoContext ctx, IOptionsMonitor<SessionPolicyOptions> opt)
        {
            _ctx = ctx;
            _opt = opt;
        }

        // =========================
        // POLICY (config từ appsettings)
        // =========================
        private SessionPolicyOptions O => _opt.CurrentValue ?? new SessionPolicyOptions();

        private TimeSpan IdleTimeout => TimeSpan.FromMinutes(Math.Max(1, O.IdleMinutes));
        private TimeSpan WarnBefore => TimeSpan.FromMinutes(Math.Max(1, O.WarnBeforeMinutes));
        private TimeSpan TouchThreshold => TimeSpan.FromSeconds(Math.Max(5, O.TouchThresholdSeconds));

        // =========================
        // CREATE SESSION (MULTI DEVICE)
        // =========================
        public async Task<LoginSession> CreateSessionAsync(
            string accountId,
            string? ip,
            string? userAgent,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            var session = new LoginSession
            {
                AccountId = accountId,
                SessionId = Guid.NewGuid().ToString("N"),
                LoginAtUtc = now,
                LastActivityAtUtc = now,
                IsActive = true,
                Ip = ip,
                UserAgent = userAgent
            };

            await _ctx.LoginSessions.InsertOneAsync(session, cancellationToken: ct);
            return session;
        }

        // =========================
        // GET SESSION STATUS
        // =========================
        public async Task<(bool ok, SessionStatus status)> GetStatusAsync(
            string sessionId,
            CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;

            var s = await _ctx.LoginSessions.Find(x => x.SessionId == sessionId)
                .FirstOrDefaultAsync(ct);

            if (s == null)
                return (false, SessionStatus.Invalid("not_found", now));

            if (!s.IsActive)
                return (false, SessionStatus.Invalid("inactive", now));

            if (s.RevokedAtUtc != null)
                return (false, SessionStatus.Invalid("revoked", now, s.RevokedReason));

            var expiresAt = s.LastActivityAtUtc.Add(IdleTimeout);
            var remaining = expiresAt - now;

            return (true, new SessionStatus
            {
                ServerNowUtc = now,
                ExpiresAtUtc = expiresAt,
                RemainingSeconds = (int)Math.Floor(remaining.TotalSeconds),
                ShouldWarn = remaining <= WarnBefore && remaining > TimeSpan.Zero,
                ShouldLogout = remaining <= TimeSpan.Zero,
                Revoked = false,
                RevokedReason = null,
                IdleMinutes = (int)IdleTimeout.TotalMinutes,
                WarnBeforeMinutes = (int)WarnBefore.TotalMinutes
            });
        }

        // =========================
        // TOUCH (UPDATE ACTIVITY)
        // =========================
        public async Task TouchAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var now = DateTime.UtcNow;

            var s = await _ctx.LoginSessions
                .Find(x => x.SessionId == sessionId && x.IsActive)
                .FirstOrDefaultAsync(ct);

            if (s == null) return;
            if (s.RevokedAtUtc != null) return;

            if (now - s.LastActivityAtUtc < TouchThreshold)
                return;

            await _ctx.LoginSessions.UpdateOneAsync(
                x => x.Id == s.Id,
                Builders<LoginSession>.Update.Set(x => x.LastActivityAtUtc, now),
                cancellationToken: ct);
        }

        // =========================
        // VALIDATE SESSION
        // =========================
        public async Task<bool> ValidateAsync(string sessionId, CancellationToken ct = default)
        {
            var (ok, status) = await GetStatusAsync(sessionId, ct);
            if (!ok) return false;

            if (status.ShouldLogout)
            {
                await ExpireBySessionIdAsync(sessionId, ct);
                return false;
            }

            return true;
        }

        // =========================
        // GET ALL SESSIONS OF USER
        // =========================
        public async Task<List<LoginSession>> GetSessionsByUserAsync(
            string userId,
            CancellationToken ct = default)
        {
            return await _ctx.LoginSessions
                .Find(x => x.AccountId == userId)
                .SortByDescending(x => x.LoginAtUtc)
                .ToListAsync(ct);
        }

        // =========================
        // LOGOUT CURRENT SESSION
        // =========================
        public async Task LogoutBySessionIdAsync(string sessionId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var now = DateTime.UtcNow;

            await _ctx.LoginSessions.UpdateOneAsync(
                x => x.SessionId == sessionId && x.IsActive,
                Builders<LoginSession>.Update
                    .Set(x => x.IsActive, false)
                    .Set(x => x.LogoutAtUtc, now),
                cancellationToken: ct);
        }

        // =========================
        // REVOKE SESSION (REMOTE LOGOUT)
        // =========================
        public async Task RevokeBySessionIdAsync(string sessionId, string reason, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var now = DateTime.UtcNow;

            await _ctx.LoginSessions.UpdateOneAsync(
                x => x.SessionId == sessionId && x.IsActive,
                Builders<LoginSession>.Update
                    .Set(x => x.IsActive, false)
                    .Set(x => x.RevokedAtUtc, now)
                    .Set(x => x.RevokedReason, reason),
                cancellationToken: ct);
        }

        // =========================
        // EXPIRE SESSION
        // =========================
        public async Task ExpireBySessionIdAsync(string sessionId, CancellationToken ct = default)
        {
            await RevokeBySessionIdAsync(
                sessionId,
                $"Expired by idle timeout ({(int)IdleTimeout.TotalMinutes}m)",
                ct);
        }
    }
}