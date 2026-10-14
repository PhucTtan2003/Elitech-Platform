namespace Elitech.Services;

public class AlertSmsService
{
    private readonly AccountService _accounts;
    private readonly FptSmsClient _sms;
    private readonly ILogger<AlertSmsService> _logger;

    public AlertSmsService(AccountService accounts, FptSmsClient sms, ILogger<AlertSmsService> logger)
    {
        _accounts = accounts;
        _sms = sms;
        _logger = logger;
    }

    public async Task<(bool ok, string? reason)> SendToUserAsync(
     string userId,
     string message,
     string requestId,
     CancellationToken ct)
    {
        var u = await _accounts.GetByIdOrUsernameAsync(userId, ct);
        if (u == null)
        {
            _logger.LogWarning("SMS BLOCK: user not found userId={UserId} requestId={RequestId}", userId, requestId);
            return (false, "User not found");
        }

        var now = DateTime.UtcNow;

        // ✅ LẤY DANH SÁCH SỐ
        var phones = (u.Phones != null && u.Phones.Count > 0)
            ? u.Phones
            : (!string.IsNullOrWhiteSpace(u.Phone)
                ? new List<string> { u.Phone }
                : new List<string>());

        phones = phones
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct()
            .ToList();

        // ====== LOG BLOCK ======
        void LogBlock(string reason)
        {
            _logger.LogWarning(
                "SMS BLOCK: {Reason} userId={UserId} username={Username} phones={Phones} verified={Verified} subActive={SubActive} exp={Exp:o} alertEnabled={AlertEnabled} isActive={IsActive} requestId={RequestId}",
                reason,
                userId,
                u.Username,
                string.Join(",", phones),
                u.PhoneVerified,
                u.SmsSubActive,
                u.SmsSubExpiresAtUtc,
                u.SmsAlertEnabled,
                u.IsActive,
                requestId
            );
        }

        // ====== VALIDATION ======
        if (!u.IsActive) return (false, "Account inactive");
        if (!u.PhoneVerified) return (false, "Phone not verified");
        if (phones.Count == 0) return (false, "No phone");
        if (!u.SmsSubActive) return (false, "SMS inactive");
        if (!u.SmsSubExpiresAtUtc.HasValue || u.SmsSubExpiresAtUtc <= now)
            return (false, "SMS expired");
        if (!u.SmsAlertEnabled) return (false, "SMS disabled");

        // ====== SEND MULTI ======
        int success = 0;

        foreach (var phone in phones)
        {
            _logger.LogInformation(
                "SMS SEND: userId={UserId} username={Username} phone={Phone} requestId={RequestId}",
                userId, u.Username, phone, requestId
            );

            var (ok, raw) = await _sms.SendDomesticAsync(phone, message, requestId, ct);

            if (ok)
            {
                success++;

                _logger.LogInformation(
                    "SMS OK: userId={UserId} username={Username} phone={Phone}",
                    userId, u.Username, phone
                );
            }
            else
            {
                _logger.LogWarning(
                    "SMS FAIL: userId={UserId} username={Username} phone={Phone} raw={Raw}",
                    userId, u.Username, phone, raw
                );
            }

            await Task.Delay(150); // tránh spam gateway
        }

        return success > 0
            ? (true, null)
            : (false, "All SMS failed");
    }

    public async Task<(bool ok, string? reason)> SendToPhoneRawAsync(
    string phone,
    string message,
    string requestId,
    CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phone)) return (false, "Phone empty");
        var (ok, raw) = await _sms.SendDomesticAsync(phone, message, requestId, ct);

        if (!ok)
            _logger.LogWarning("SMS failed phone={Phone}, raw={Raw}", phone, raw);

        return ok ? (true, null) : (false, raw);
    }


}
