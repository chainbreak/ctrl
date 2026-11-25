using Ctrl.Api.Domain;
using Ctrl.Api.Filters;
using Ctrl.Api.Middleware;
using Ctrl.Api.Persistence;
using Ctrl.Api.Security;
using Ctrl.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var bootstrapCommand = AdminBootstrapCommand.Parse(args);
var builder = WebApplication.CreateBuilder(bootstrapCommand is null ? args : Array.Empty<string>());

var databaseProvider = builder.Configuration["Database:Provider"]
    ?? (builder.Environment.IsDevelopment() ? "Sqlite" : "PostgreSQL");
var connectionString = builder.Configuration.GetConnectionString("Default");

builder.Services.AddDbContext<CtrlDbContext>(options =>
{
    if (databaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlite(connectionString ?? "Data Source=ctrl-dev.db");
        return;
    }

    if (databaseProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set ConnectionStrings__Default for PostgreSQL.");
        }

        options.UseNpgsql(connectionString);
        return;
    }

    throw new InvalidOperationException($"Unsupported database provider '{databaseProvider}'.");
});

builder.Services
    .AddIdentity<AppUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<CtrlDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.AdminMfa, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new AdminMfaRequirement());
    });
});
builder.Services.AddScoped<IAuthorizationHandler, AdminMfaHandler>();
builder.Services.AddScoped<IGiftService, GiftService>();
builder.Services.AddHttpClient<IExchangeRateService, CbrExchangeRateService>(client =>
{
    client.BaseAddress = new Uri("https://www.cbr-xml-daily.ru/");
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddExceptionHandler<ExchangeRatesExceptionHandler>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-ctrl-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.Configure<CookieAuthenticationOptions>(
    IdentityConstants.TwoFactorUserIdScheme,
    options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        options.SlidingExpiration = false;
    });

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-ctrl-csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});
builder.Services.AddScoped<ApiAntiforgeryFilter>();
builder.Services.AddControllers(options =>
    options.Filters.AddService<ApiAntiforgeryFilter>());
builder.Services.AddOpenApi();
builder.Services.AddOutputCache();
builder.Services.AddProblemDetails();

var app = builder.Build();

if (bootstrapCommand is not null)
{
    await bootstrapCommand.ExecuteAsync(app.Services, app.Environment);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; connect-src 'self'; script-src 'self'; style-src 'self'";
    if (!app.Environment.IsDevelopment())
    {
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<CtrlDbContext>();
    await database.Database.EnsureCreatedAsync();
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

await app.RunAsync();
