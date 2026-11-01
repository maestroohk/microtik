using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Platform;

public class StaffForm
{
    [Required, StringLength(120)]
    public string FullName { get; set; } = "";

    [Required, EmailAddress, StringLength(200)]
    public string Email { get; set; } = "";

    public UserRole Role { get; set; } = UserRole.Support;
}

public class StaffService(KiboDb kiboDb, CurrentUser currentUser, IPasswordHasher<User> hasher, AuditLogger audit, TimeProvider clock)
{
    public async Task<List<User>> ListAsync()
    {
        Demand();
        await using var db = kiboDb.ForCurrentUser();
        return await db.Users.AsNoTracking().Where(u => !u.IsPlatformAdmin).OrderBy(u => u.FullName).ToListAsync();
    }

    public async Task<OperationResult<string>> InviteAsync(StaffForm form)
    {
        Demand();
        if (form.Role == UserRole.Owner && currentUser.Role != UserRole.Owner && !currentUser.IsPlatformAdmin)
        {
            return OperationResult<string>.Fail("Only an owner can add another owner.");
        }

        var email = form.Email.Trim().ToLowerInvariant();
        await using var db = kiboDb.ForCurrentUser();
        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
        {
            return OperationResult<string>.Fail("A user with this email already exists.");
        }

        var temporaryPassword = SecretProtector.NewSecret(12);
        var user = new User
        {
            TenantId = db.TenantScope,
            Email = email,
            FullName = form.FullName.Trim(),
            Role = form.Role,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        user.PasswordHash = hasher.HashPassword(user, temporaryPassword);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        await audit.WriteAsync("staff.added", "User", user.Id.ToString(), $"{user.Email} as {user.Role}");
        return OperationResult<string>.Ok(temporaryPassword);
    }

    public async Task<OperationResult<bool>> UpdateAsync(Guid userId, UserRole role, bool active)
    {
        Demand();
        await using var db = kiboDb.ForCurrentUser();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && !u.IsPlatformAdmin);
        if (user == null)
        {
            return OperationResult<bool>.Fail("User not found.");
        }

        if (user.Id == currentUser.UserId && (!active || role != user.Role))
        {
            return OperationResult<bool>.Fail("You cannot change your own role or deactivate yourself.");
        }

        var removingOwner = user.Role == UserRole.Owner && (role != UserRole.Owner || !active);
        if (removingOwner && await db.Users.CountAsync(u => u.Role == UserRole.Owner && u.IsActive) <= 1)
        {
            return OperationResult<bool>.Fail("An operator must keep at least one active owner.");
        }

        if ((role == UserRole.Owner || user.Role == UserRole.Owner) && currentUser.Role != UserRole.Owner && !currentUser.IsPlatformAdmin)
        {
            return OperationResult<bool>.Fail("Only an owner can change owners.");
        }

        user.Role = role;
        user.IsActive = active;
        await db.SaveChangesAsync();
        await audit.WriteAsync("staff.updated", "User", user.Id.ToString(), $"{user.Email}: {role}, {(active ? "active" : "deactivated")}");
        return OperationResult<bool>.Ok(true);
    }

    private void Demand()
    {
        if (!currentUser.Can(Permissions.ManageStaff))
        {
            throw new UnauthorizedAccessException("Missing permission to manage staff.");
        }
    }
}
