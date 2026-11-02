using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Platform;

public static class PlatformBootstrap
{
    public static async Task<string> CreateAdminAsync(AppDbContext db, IPasswordHasher<User> hasher, string? email, string? password,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password) || password.Length < 16)
        {
            return "Set KIBO_BOOTSTRAP_ADMIN_EMAIL and KIBO_BOOTSTRAP_ADMIN_PASSWORD (at least 16 characters).";
        }

        if (await db.Users.AnyAsync(u => u.IsPlatformAdmin))
        {
            return "A platform administrator already exists; nothing was changed.";
        }

        var user = new User
        {
            Email = email.Trim().ToLowerInvariant(),
            FullName = "Platform administrator",
            Role = UserRole.Owner,
            IsPlatformAdmin = true,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        db.AuditLogs.Add(new AuditLog
        {
            UserName = "bootstrap", Action = "platform.admin_created", EntityType = "User", EntityId = user.Id.ToString(),
            CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
        return $"Created platform administrator {user.Email}. Sign in and turn on two-step sign-in before managing operators.";
    }
}
