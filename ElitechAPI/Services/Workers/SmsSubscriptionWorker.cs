namespace Elitech.Services.Workers
{
    public class SmsSubscriptionWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SmsSubscriptionWorker> _logger;

        public SmsSubscriptionWorker(IServiceScopeFactory scopeFactory, ILogger<SmsSubscriptionWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var accSvc = scope.ServiceProvider.GetRequiredService<AccountService>();

                    var n = await accSvc.DisableExpiredSmsSubscriptionsAsync(stoppingToken);
                    if (n > 0) _logger.LogInformation("Disabled {N} expired SMS subscriptions", n);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SmsSubscriptionWorker error");
                }

                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            }
        }
    }
}
