using DinkToPdf;
using DinkToPdf.Contracts;
using Elitech.Data;
using Elitech.Hubs;
using Elitech.Infrastructure;
using Elitech.Middleware;
using Elitech.Models;
using Elitech.Models.Alerts;
using Elitech.Services;
using Elitech.Services.SmsSchedule;
using Elitech.Services.Workers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

var builder = WebApplication.CreateBuilder(args);

// =========================
// HOST
// =========================
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior =
        BackgroundServiceExceptionBehavior.Ignore;
});

// =========================
// DINK TO PDF
// =========================
var context = new CustomAssemblyLoadContext();
var baseDir = AppContext.BaseDirectory;

var libName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? "libwkhtmltox.dll"
    : "libwkhtmltox.so";

var libPath = Path.Combine(baseDir, libName);

if (!File.Exists(libPath))
{
    Console.WriteLine($"❌ KHÔNG tìm thấy {libName} tại: {libPath}");
    Console.WriteLine("👉 PDF sẽ KHÔNG hoạt động");
}
else
{
    try
    {
        context.LoadUnmanagedLibrary(libPath);

        builder.Services.AddSingleton<IConverter>(
            new SynchronizedConverter(new PdfTools())
        );

        Console.WriteLine($"✅ Loaded {libName} OK");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Load {libName} FAILED: {ex.Message}");
    }
}

// =========================
// CONFIGURATION
// =========================
builder.Services.Configure<MongoOptions>(
    builder.Configuration.GetSection("Mongo"));

builder.Services.Configure<FptSmsOptions>(
    builder.Configuration.GetSection("FptSms"));

builder.Services.Configure<AlertNotifyOptions>(
    builder.Configuration.GetSection("AlertNotify"));

builder.Services.Configure<SessionPolicyOptions>(
    builder.Configuration.GetSection("SessionPolicy"));

// MongoContext dùng chung toàn ứng dụng.
builder.Services.AddSingleton<MongoContext>();

// =========================
// HTTP CLIENTS
// =========================
builder.Services.AddHttpClient("ElitechMain", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("ElitechAlarm", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<FptSmsClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// =========================
// CACHE
// =========================
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CacheService>();

// =========================
// ELITECH OPTIONS
// =========================
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetSection("Elitech");

    return new ElitechOptions
    {
        BaseUrl = config["BaseUrl"] ?? "",
        KeyId = config["KeyId"] ?? "",
        KeySecret = config["KeySecret"] ?? "",
        UserName = config["UserName"] ?? "",
        Password = config["Password"] ?? ""
    };
});

builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetSection("ElitechAlarm");

    return new ElitechAlarmOptions
    {
        BaseUrl = config["BaseUrl"] ?? "",
        KeyId = config["KeyId"] ?? "",
        KeySecret = config["KeySecret"] ?? "",
        UserName = config["UserName"] ?? "",
        Password = config["Password"] ?? ""
    };
});

// =========================
// ELITECH V2 + MONGO MAPPING
// =========================
//
// Dependency chain:
// ElitechApiClient
//   -> ElitechV2LegacyProjectionService
//      -> ElitechDeviceParamMappingService
//         -> MongoContext
//
// Các service trong chuỗi này đều là Singleton để ElitechApiClient
// có thể được dùng an toàn bởi cả Controller và BackgroundService.
//
builder.Services.AddSingleton<ElitechDeviceParamMappingService>();
builder.Services.AddSingleton<ElitechV2LegacyProjectionService>();
builder.Services.AddSingleton<ElitechApiClient>();

// =========================
// APPLICATION SERVICES
// =========================
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<LoginSessionService>();
builder.Services.AddScoped<ElitechDeviceAssignmentService>();
builder.Services.AddScoped<AlertSmsService>();
builder.Services.AddScoped<ElitechAlertRuleService>();
builder.Services.AddSingleton<ElitechAlertEngine>();
builder.Services.AddScoped<ElitechAlertEventService>();

builder.Services.AddScoped<SmsTemperatureScheduleService>();
builder.Services.AddScoped<SmsTemperatureSender>();

// =========================
// WORKERS
// =========================
builder.Services.AddHostedService<
    Elitech.Services.Workers.SmsTemperatureScheduleWorker>();

builder.Services.AddHostedService<SmsSubscriptionWorker>();

builder.Services.AddHostedService<
    Elitech.Workers.ElitechAlertWorker>();

builder.Services.AddHostedService<ElitechAlarmRealtimeWorker>();

// =========================
// SIGNALR
// =========================
builder.Services.AddSignalR();

// =========================
// MVC + ROUTING
// =========================
builder.Services.AddRouting(options =>
{
    options.LowercaseUrls = true;
    options.AppendTrailingSlash = false;
});

builder.Services.AddControllersWithViews();

// =========================
// AUTHENTICATION
// =========================
builder.Services
    .AddAuthentication(
        CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Elitech.Auth";
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login/Denied";

        options.SlidingExpiration = true;

        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy =
            CookieSecurePolicy.Always;
        options.Cookie.SameSite =
            SameSiteMode.Lax;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// =========================
// FORWARDED HEADERS
// =========================
app.UseForwardedHeaders(
    new ForwardedHeadersOptions
    {
        ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto
    });

// =========================
// SEED
// =========================
using (var scope = app.Services.CreateScope())
{
    var accountService = scope.ServiceProvider
        .GetRequiredService<AccountService>();

    await accountService.SeedAsync();
}

// =========================
// ERROR HANDLING
// =========================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// =========================
// MIDDLEWARE
// =========================
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseMiddleware<SessionGuardMiddleware>();
app.UseAuthorization();

// =========================
// ENDPOINTS
// =========================
app.MapControllers();

app.MapHub<ElitechAlarmHub>("/hubs/alarm");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Index}/{id?}");

app.Run();