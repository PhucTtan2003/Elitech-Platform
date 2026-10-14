using Elitech.Services;
using Elitech.Services.SmsSchedule;

namespace Elitech.Services.Workers;

public class SmsTemperatureScheduleWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SmsTemperatureScheduleWorker> _logger;

    public SmsTemperatureScheduleWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SmsTemperatureScheduleWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SmsTemperatureScheduleWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmsTemperatureScheduleWorker loop error");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var tz = ResolveTimeZone("Asia/Ho_Chi_Minh");
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);

        var hhmm = nowLocal.ToString("HH:mm");
        var dueKey = $"{nowLocal:yyyyMMdd}|{hhmm}";

        using var scope = _scopeFactory.CreateScope();

        var scheduleService = scope.ServiceProvider.GetRequiredService<SmsTemperatureScheduleService>();
        var sender = scope.ServiceProvider.GetRequiredService<SmsTemperatureSender>();

        var candidates = await scheduleService.GetDueCandidatesAsync(hhmm, ct);

        if (candidates.Count == 0)
            return;

        foreach (var schedule in candidates)
        {
            if (string.IsNullOrWhiteSpace(schedule.Id))
                continue;

            var claimed = await scheduleService.TryClaimAsync(schedule.Id, dueKey, ct);
            if (!claimed)
                continue;

            await sender.SendAsync(schedule, dueKey, hhmm, ct);
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Local;
        }
    }
}