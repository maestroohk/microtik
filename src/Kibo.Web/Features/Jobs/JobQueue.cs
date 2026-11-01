using System.Text.Json;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Jobs;

public static class JobKinds
{
    public const string DisconnectUser = "disconnect-user";
    public const string VerifyPayment = "verify-payment";
}

public record DisconnectUserPayload(Guid TenantId, string Username, string Reason);

public record VerifyPaymentPayload(Guid PaymentId);

public static class JobQueue
{
    public static void Enqueue<T>(AppDbContext db, string kind, T payload, DateTime now, Guid? tenantId,
        string? deduplicationKey = null, TimeSpan? delay = null, int maxAttempts = 5)
    {
        db.BackgroundJobs.Add(new BackgroundJob
        {
            TenantId = tenantId,
            Kind = kind,
            Payload = JsonSerializer.Serialize(payload),
            DeduplicationKey = deduplicationKey,
            Status = JobStatus.Pending,
            MaxAttempts = maxAttempts,
            RunAfter = now + (delay ?? TimeSpan.Zero),
            CreatedAt = now
        });
    }

    public static async Task<bool> EnqueueUniqueAsync<T>(AppDbContext db, string kind, T payload, DateTime now,
        Guid? tenantId, string deduplicationKey, TimeSpan? delay = null, int maxAttempts = 5)
    {
        var alreadyQueued = await db.BackgroundJobs.AnyAsync(j =>
            j.DeduplicationKey == deduplicationKey &&
            (j.Status == JobStatus.Pending || j.Status == JobStatus.Running));
        if (alreadyQueued)
        {
            return false;
        }

        Enqueue(db, kind, payload, now, tenantId, deduplicationKey, delay, maxAttempts);
        return true;
    }
}
