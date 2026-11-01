using System.Security.Claims;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Auth;

public static class AuthEndpoints
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/account/login", LoginAsync);
        app.MapPost("/account/logout", async (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.LocalRedirect("/");
            }

            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/login");
        });
    }

    private static async Task<IResult> LoginAsync(HttpContext http, KiboDb kiboDb, TimeProvider clock,
        AuditLogger audit, IPasswordHasher<User> hasher, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            return Results.LocalRedirect("/login?error=expired");
        }

        var form = await http.Request.ReadFormAsync();
        var email = form["email"].ToString().Trim().ToLowerInvariant();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        var now = clock.GetUtcNow().UtcDateTime;

        await using var db = kiboDb.ForSystem();
        var user = await db.Users.Include(u => u.Tenant).FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || !user.IsActive)
        {
            return Results.LocalRedirect("/login?error=invalid");
        }

        if (user.LockedUntil > now)
        {
            return Results.LocalRedirect("/login?error=locked");
        }

        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntil = now.Add(LockoutDuration);
                user.FailedLoginCount = 0;
            }

            await db.SaveChangesAsync();
            await audit.WriteAsync("auth.login_failed", "User", user.Id.ToString(), tenantId: user.TenantId);
            return Results.LocalRedirect("/login?error=invalid");
        }

        if (user.Tenant is { Status: TenantStatus.Suspended } && !user.IsPlatformAdmin)
        {
            return Results.LocalRedirect("/login?error=suspended");
        }

        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
        }

        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString())
        };
        if (user.TenantId != null)
        {
            claims.Add(new Claim(KiboClaims.TenantId, user.TenantId.Value.ToString()));
        }

        if (user.IsPlatformAdmin)
        {
            claims.Add(new Claim(KiboClaims.PlatformAdmin, "true"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var destination = Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) && returnUrl.StartsWith('/')
            && !returnUrl.StartsWith("//")
            ? returnUrl
            : user.IsPlatformAdmin && user.TenantId == null ? "/platform" : "/";
        return Results.LocalRedirect(destination);
    }
}
