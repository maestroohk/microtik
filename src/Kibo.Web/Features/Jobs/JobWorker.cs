using System.Text.Json;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Payments;
using Kibo.Web.Features.Sessions;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Jobs;

public class JobWorker(IServiceScopeFactory scopeFactory, TimeProvider clock, ILogger<JobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try
            {
                while (await RunNextJobAsync(stoppingToken))
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job worker cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> RunNextJobAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var kiboDb = scope.ServiceProvider.GetRequiredService<KiboDb>();
        await using var db = kiboDb.ForSystem();
        var now = clock.GetUtcNow().UtcDateTime;

        var job = await ClaimAsync(db, now, ct);
        if (job == null)
        {
            return false;
        }

        try
        {
            await ExecuteJobAsync(scope.ServiceProvider, job, ct);
            job.Status = JobStatus.Succeeded;
            job.CompletedAt = clock.GetUtcNow().UtcDateTime;
            job.LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
            if (job.Attempts >= job.MaxAttempts)
            {
                job.Status = JobStatus.Failed;
                job.CompletedAt = clock.GetUtcNow().UtcDateTime;
                logger.LogWarning("Job {JobId} ({Kind}) failed permanently: {Error}", job.Id, job.Kind, ex.Message);
            }
            else
            {
                job.Status = JobStatus.Pending;
                job.RunAfter = clock.GetUtcNow().UtcDateTime + RetryDelay(job.Attempts);
                logger.LogInformation("Job {JobId} ({Kind}) attempt {Attempt} failed: {Error}", job.Id, job.Kind, job.Attempts, ex.Message);
            }
        }

        job.LockedUntil = null;
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private static async Task<BackgroundJob?> ClaimAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var job = await db.BackgroundJobs
            .FromSqlInterpolated($@"
                SELECT * FROM background_jobs
                WHERE (status = 0 AND run_after <= {now}) OR (status = 1 AND locked_until < {now})
                ORDER BY run_after
                LIMIT 1
                FOR UPDATE SKIP LOCKED")
            .FirstOrDefaultAsync(ct);

        if (job == null)
        {
            return null;
        }

        job.Status = JobStatus.Running;
        job.Attempts++;
        job.LockedUntil = now + LockDuration;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return job;
    }

    private static async Task ExecuteJobAsync(IServiceProvider services, BackgroundJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case JobKinds.DisconnectUser:
                var disconnect = JsonSerializer.Deserialize<DisconnectUserPayload>(job.Payload)!;
                await services.GetRequiredService<SessionService>()
                    .DisconnectUserAsync(disconnect.TenantId, disconnect.Username, disconnect.Reason, ct);
                break;
            case JobKinds.VerifyPayment:
                var verify = JsonSerializer.Deserialize<VerifyPaymentPayload>(job.Payload)!;
                await services.GetRequiredService<PaymentService>().VerifyAndSettleAsync(verify.PaymentId, ct);
                break;
            case JobKinds.ThrottleUser:
                var throttle = JsonSerializer.Deserialize<ThrottleUserPayload>(job.Payload)!;
                await services.GetRequiredService<SessionService>()
                    .ChangeRateAsync(throttle.TenantId, throttle.Username, throttle.RateLimit, ct);
                break;
            default:
                throw new InvalidOperationException($"Unknown job kind {job.Kind}.");
        }
    }

    private static TimeSpan RetryDelay(int attempts) => TimeSpan.FromSeconds(Math.Min(900, 10 * Math.Pow(3, attempts - 1)));
}
