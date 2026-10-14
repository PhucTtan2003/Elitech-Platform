using Elitech.Models.Sms;
using Elitech.Services.SmsSchedule;

namespace Elitech.Services;

public class SmsTemperatureSender
{
    private readonly ElitechApiClient _elitechApi;
    private readonly FptSmsClient _smsClient;
    private readonly SmsTemperatureScheduleService _scheduleService;
    private readonly ILogger<SmsTemperatureSender> _logger;

    public SmsTemperatureSender(
        ElitechApiClient elitechApi,
        FptSmsClient smsClient,
        SmsTemperatureScheduleService scheduleService,
        ILogger<SmsTemperatureSender> logger)
    {
        _elitechApi = elitechApi;
        _smsClient = smsClient;
        _scheduleService = scheduleService;
        _logger = logger;
    }

    public async Task SendAsync(
        SmsTemperatureSchedule schedule,
        string dueKey,
        string dueTime,
        CancellationToken ct = default)
    {
        var requestId = BuildRequestId(schedule, dueKey);

        var log = new SmsTemperatureSendLog
        {
            ScheduleId = schedule.Id ?? "",
            UserId = schedule.UserId,
            DeviceGuid = schedule.DeviceGuid,
            DeviceName = schedule.DeviceName,
            Phone = schedule.Phone,
            DueKey = dueKey,
            DueTime = dueTime,
            RequestId = requestId
        };

        try
        {
            var rt = await _elitechApi.GetRealTimeDataAsync(schedule.DeviceGuid, ct);

            if (rt == null || rt.code != 0)
                throw new InvalidOperationException($"Elitech realtime failed. code={rt?.code}, msg={rt?.msg ?? rt?.message ?? rt?.error}");

            var item = rt.data?.FirstOrDefault();
            if (item == null)
                throw new InvalidOperationException("Elitech realtime empty data");

            var deviceName = !string.IsNullOrWhiteSpace(schedule.DeviceName)
                ? schedule.DeviceName
                : item.deviceName ?? schedule.DeviceGuid;

            var sampleTime = UnixToLocalString(item.lastSessionTime, schedule.TimeZoneId);

            var message = BuildMessage(
                deviceName: deviceName,
                deviceGuid: schedule.DeviceGuid,
                tmp1: item.tmp1,
                tmp2: item.tmp2,
                tmp3: item.tmp3,
                tmp4: item.tmp4,
                hum1: item.hum1,
                hum2: item.hum2,
                sampleTime: sampleTime);

            log.Message = message;

            var sent = await _smsClient.SendDomesticAsync(
                phone: schedule.Phone,
                messagePlain: message,
                requestId: requestId,
                ct: ct);

            log.Ok = sent.ok;
            log.RawResponse = sent.raw;

            if (sent.ok)
            {
                await _scheduleService.MarkSentAsync(schedule.Id!, dueKey, ct);
            }
            else
            {
                log.Error = "FPT SMS returned non-success HTTP status";
                _logger.LogWarning("Scheduled temp SMS failed. scheduleId={ScheduleId}, raw={Raw}",
                    schedule.Id, sent.raw);
            }
        }
        catch (Exception ex)
        {
            log.Ok = false;
            log.Error = ex.Message;

            _logger.LogError(ex,
                "Send scheduled temp SMS error. scheduleId={ScheduleId}, deviceGuid={DeviceGuid}",
                schedule.Id,
                schedule.DeviceGuid);
        }
        finally
        {
            await _scheduleService.InsertLogAsync(log, ct);
        }
    }

    private static string BuildMessage(
        string deviceName,
        string deviceGuid,
        string? tmp1,
        string? tmp2,
        string? tmp3,
        string? tmp4,
        string? hum1,
        string? hum2,
        string sampleTime)
    {
        // Nên đổi câu này theo template đã đăng ký với FPT.
        // Dùng không dấu để giảm rủi ro lỗi template/unicode.
        var temps = new List<string>();

        if (!string.IsNullOrWhiteSpace(tmp1)) temps.Add($"T1 {tmp1}");
        if (!string.IsNullOrWhiteSpace(tmp2)) temps.Add($"T2 {tmp2}");
        if (!string.IsNullOrWhiteSpace(tmp3)) temps.Add($"T3 {tmp3}");
        if (!string.IsNullOrWhiteSpace(tmp4)) temps.Add($"T4 {tmp4}");

        var hums = new List<string>();
        if (!string.IsNullOrWhiteSpace(hum1)) hums.Add($"H1 {hum1}");
        if (!string.IsNullOrWhiteSpace(hum2)) hums.Add($"H2 {hum2}");

        var tempText = temps.Count > 0 ? string.Join(", ", temps) : "khong co du lieu nhiet do";
        var humText = hums.Count > 0 ? " | " + string.Join(", ", hums) : "";

        return $"Nhiet do gan nhat thiet bi {deviceName}: {tempText}{humText}. Thoi gian cap nhat: {sampleTime}.";
    }

    private static string BuildRequestId(SmsTemperatureSchedule schedule, string dueKey)
    {
        var guidPart = (schedule.DeviceGuid ?? "")
            .Replace("-", "")
            .Replace(" ", "");

        var keyPart = dueKey
            .Replace("|", "-")
            .Replace(":", "");

        var raw = $"TEMP-{guidPart}-{keyPart}";

        return raw.Length <= 100 ? raw : raw[..100];
    }

    private static string UnixToLocalString(long? unix, string? timeZoneId)
    {
        if (unix == null || unix <= 0)
            return "khong ro";

        DateTimeOffset dto = unix > 3_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(unix.Value)
            : DateTimeOffset.FromUnixTimeSeconds(unix.Value);

        var tz = ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(dto, tz);

        return local.ToString("dd/MM/yyyy HH:mm:ss");
    }

    private static TimeZoneInfo ResolveTimeZone(string? id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                string.IsNullOrWhiteSpace(id) ? "Asia/Ho_Chi_Minh" : id);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}