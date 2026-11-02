using Kibo.Web.Data;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Auth;

public class MfaService(KiboDb kiboDb, CurrentUser currentUser, SecretProtector secrets, AuditLogger audit, TimeProvider clock)
{
    public async Task<bool> IsEnabledAsync()
    {
        await using var db = kiboDb.ForSystem();
        return await db.Users.AnyAsync(u => u.Id == currentUser.UserId && u.TotpSecretProtected != null);
    }

    public async Task<OperationResult<bool>> EnableAsync(string secret, string code)
    {
        var step = Totp.Verify(secret, code, clock.GetUtcNow(), 0);
        if (step == null)
        {
            return OperationResult<bool>.Fail("That code did not match. Check the time on your phone and try the newest code.");
        }

        await using var db = kiboDb.ForSystem();
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId);
        user.TotpSecretProtected = secrets.Protect(secret);
        user.TotpLastStep = step.Value;
        user.MfaEnabledAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        await audit.WriteAsync("auth.mfa_enabled", "User", user.Id.ToString(), tenantId: user.TenantId);
        return OperationResult<bool>.Ok(true);
    }

    public async Task<OperationResult<bool>> DisableAsync(string code)
    {
        await using var db = kiboDb.ForSystem();
        var user = await db.Users.FirstAsync(u => u.Id == currentUser.UserId);
        if (user.TotpSecretProtected == null)
        {
            return OperationResult<bool>.Ok(true);
        }

        if (Totp.Verify(secrets.Unprotect(user.TotpSecretProtected), code, clock.GetUtcNow(), user.TotpLastStep) == null)
        {
            return OperationResult<bool>.Fail("Enter a current code to turn off two-step sign-in.");
        }

        user.TotpSecretProtected = null;
        user.MfaEnabledAt = null;
        await db.SaveChangesAsync();
        await audit.WriteAsync("auth.mfa_disabled", "User", user.Id.ToString(), tenantId: user.TenantId);
        return OperationResult<bool>.Ok(true);
    }
}
