using Kibo.Web.Components;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Auth;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Vouchers;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Packages;
using Kibo.Web.Features.Payments;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Portal;
using Kibo.Web.Features.Pppoe;
using Kibo.Web.Features.Reports;
using Kibo.Web.Features.Sites;
using Kibo.Web.Features.Routers;
using Kibo.Web.Features.Sessions;
using Kibo.Web.Features.Subscriptions;
using Kibo.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Kibo")
    ?? throw new InvalidOperationException("ConnectionStrings:Kibo is not configured. See docs/ARCHITECTURE.md.");

builder.Services.AddDbContextFactory<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

var keysPath = builder.Configuration["Kibo:DataProtectionKeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Kibo");
if (!string.IsNullOrEmpty(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "kibo.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<CurrentUser>(sp =>
{
    var user = new CurrentUser();
    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    if (http != null)
    {
        user.Load(http.User);
        return user;
    }

    try
    {
        var state = sp.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
        if (state.IsCompletedSuccessfully)
        {
            user.Load(state.Result.User);
        }
    }
    catch (InvalidOperationException)
    {
    }

    return user;
});
builder.Services.AddScoped<KiboDb>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<TenantService>();
builder.Services.AddScoped<RouterService>();
builder.Services.AddScoped<RouterHealthChecker>();
builder.Services.AddSingleton<RouterLiveUpdates>();
builder.Services.AddScoped<RouterOsClient>();
builder.Services.AddScoped<PackageService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<SubscriptionLifecycle>();
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<PortalService>();
builder.Services.AddScoped<DevelopmentSeeder>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<SiteService>();
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<VoucherService>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddScoped<AuditQuery>();
builder.Services.AddScoped<PppoeService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<StaffService>();
builder.Services.AddScoped<RenewalService>();
builder.Services.Configure<OnboardingOptions>(builder.Configuration.GetSection("Kibo:Onboarding"));

builder.Services.AddHttpClient(RouterOsClient.StrictClientName);
builder.Services.AddHttpClient(RouterOsClient.TrustingClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });

builder.Services.Configure<SimulatedMobileMoneyOptions>(builder.Configuration.GetSection("Payments:SimulatedMobileMoney"));
builder.Services.AddHttpClient<IPaymentProvider, SimulatedMobileMoneyProvider>(client => client.Timeout = TimeSpan.FromSeconds(15));

builder.Services.AddHostedService<RouterMonitorWorker>();
builder.Services.AddHostedService<SubscriptionLifecycleWorker>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<RenewalWorker>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var path = http.Request.Path.Value ?? "";
        var isSensitivePost = HttpMethods.IsPost(http.Request.Method) &&
                              (path.StartsWith("/p/") || path.StartsWith("/account/login"));
        if (!isSensitivePost)
        {
            return RateLimitPartition.GetNoLimiter("unlimited");
        }

        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{client}:{path.Split('/').ElementAtOrDefault(3)}", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapPaymentEndpoints();
app.MapPortalEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await using (var scope = app.Services.CreateAsyncScope())
{
    var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();
    await using var db = new AppDbContext(options, null);
    await DatabaseSetup.MigrateAsync(db);

    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync();
    }
}

app.Run();

public partial class Program;
