using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Jobs;
using Kibo.Web.Features.Platform;
using Kibo.Web.Features.Subscriptions;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Vouchers;

public class VoucherBatchForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required]
    public Guid PackageId { get; set; }

    [Range(1, 1000)]
    public int Quantity { get; set; } = 20;

    [Range(1, 365)]
    public int RedeemWithinDays { get; set; } = 60;

    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }

    [Range(0, 100_000_000)]
    public decimal? UnitPrice { get; set; }
}

public record BatchSummary(VoucherBatch Batch, string PackageName, string? AgentName, int Available, int Sold, int Redeemed,
    int Cancelled, int Expired);

public class VoucherService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;

    public async Task<List<BatchSummary>> ListBatchesAsync()
    {
        Demand(Permissions.ManageVouchers, Permissions.SellVouchers);
        await using var db = kiboDb.ForCurrentUser();
        var batches = await db.VoucherBatches.AsNoTracking().Include(b => b.Package).OrderByDescending(b => b.CreatedAt).ToListAsync();
        var counts = await db.Vouchers.AsNoTracking().GroupBy(v => new { v.BatchId, v.Status })
            .Select(g => new { g.Key.BatchId, g.Key.Status, Count = g.Count() }).ToListAsync();
        var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);

        int Count(Guid batch, VoucherStatus status) => counts.FirstOrDefault(c => c.BatchId == batch && c.Status == status)?.Count ?? 0;

        return batches.Select(b => new BatchSummary(b, b.Package?.Name ?? "—",
            b.AgentId == null ? null : agents.GetValueOrDefault(b.AgentId.Value),
            Count(b.Id, VoucherStatus.Available), Count(b.Id, VoucherStatus.Sold), Count(b.Id, VoucherStatus.Redeemed),
            Count(b.Id, VoucherStatus.Cancelled), Count(b.Id, VoucherStatus.Expired))).ToList();
    }

    public async Task<(VoucherBatch? Batch, List<Voucher> Vouchers, Site? Site)> GetBatchAsync(Guid batchId)
    {
        Demand(Permissions.ManageVouchers, Permissions.SellVouchers);
        await using var db = kiboDb.ForCurrentUser();
        var batch = await db.VoucherBatches.AsNoTracking().Include(b => b.Package).FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch == null)
        {
            return (null, [], null);
        }

        var vouchers = await db.Vouchers.AsNoTracking().Where(v => v.BatchId == batchId).OrderBy(v => v.Code).ToListAsync();
        var site = batch.SiteId == null
            ? await db.Sites.AsNoTracking().OrderBy(s => s.CreatedAt).FirstOrDefaultAsync()
            : await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == batch.SiteId);
        return (batch, vouchers, site);
    }

    public async Task<OperationResult<VoucherBatch>> GenerateAsync(VoucherBatchForm form)
    {
        Demand(Permissions.ManageVouchers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var tenantId = db.TenantScope!.Value;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId);
        var package = await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == form.PackageId && p.IsActive);
        if (package == null)
        {
            return OperationResult<VoucherBatch>.Fail("Choose an active package.");
        }

        if (package.ServiceType != ServiceType.Hotspot)
        {
            return OperationResult<VoucherBatch>.Fail("Vouchers are for hotspot packages.");
        }

        if (form.AgentId != null && !await db.Agents.AnyAsync(a => a.Id == form.AgentId && a.IsActive))
        {
            return OperationResult<VoucherBatch>.Fail("Choose an active agent.");
        }

        var batch = new VoucherBatch
        {
            Name = form.Name.Trim(),
            PackageId = package.Id,
            SiteId = form.SiteId,
            AgentId = form.AgentId,
            Quantity = form.Quantity,
            UnitPrice = form.UnitPrice ?? package.Price,
            RedeemBy = now.AddDays(form.RedeemWithinDays),
            CreatedBy = currentUser.Email,
            CreatedAt = now
        };
        db.VoucherBatches.Add(batch);

        var existingCodes = new HashSet<string>(await db.Vouchers.Select(v => v.Code).ToListAsync());
        existingCodes.UnionWith(await db.AccessAccounts.Select(a => a.Username).ToListAsync());

        for (var i = 0; i < form.Quantity; i++)
        {
            string code;
            do
            {
                code = SecretProtector.NewCode(CodeLength, CodeAlphabet);
            } while (!existingCodes.Add(code));

            var account = new AccessAccount
            {
                TenantId = tenantId,
                Kind = AccessAccountKind.Voucher,
                Username = code,
                Password = code,
                CreatedAt = now
            };
            var subscription = Entitlements.CreateFromPackage(package, account, now, SubscriptionSource.Voucher, code, false,
                batch.UnitPrice);
            subscription.ActivateBy = batch.RedeemBy;
            if (subscription.Status == SubscriptionStatus.Active)
            {
                subscription.Status = SubscriptionStatus.Unused;
                subscription.ActivatedAt = null;
                subscription.ExpiresAt = null;
            }

            db.AccessAccounts.Add(account);
            db.Subscriptions.Add(subscription);
            db.Vouchers.Add(new Voucher
            {
                BatchId = batch.Id,
                Code = code,
                AccessAccountId = account.Id,
                SubscriptionId = subscription.Id,
                AgentId = form.AgentId
            });
            db.RadiusAccess.Add(Entitlements.BuildRadiusAccess(account, subscription, tenant, null, now));
        }

        await db.SaveChangesAsync();
        await audit.WriteAsync("voucher_batch.generated", "VoucherBatch", batch.Id.ToString(),
            $"{batch.Quantity} × {package.Name} at {batch.UnitPrice}");
        return OperationResult<VoucherBatch>.Ok(batch);
    }

    public async Task<OperationResult<int>> SellAsync(Guid batchId, int quantity, Guid? agentId)
    {
        Demand(Permissions.SellVouchers);
        if (quantity < 1)
        {
            return OperationResult<int>.Fail("Sell at least one voucher.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var batch = await db.VoucherBatches.FirstOrDefaultAsync(b => b.Id == batchId);
        if (batch == null)
        {
            return OperationResult<int>.Fail("Batch not found.");
        }

        var sellingAgentId = agentId ?? batch.AgentId;
        var agent = sellingAgentId == null ? null : await db.Agents.FirstOrDefaultAsync(a => a.Id == sellingAgentId && a.IsActive);
        if (sellingAgentId != null && agent == null)
        {
            return OperationResult<int>.Fail("The agent is inactive.");
        }

        if (agent != null)
        {
            var outstanding = await AgentBalanceAsync(db, agent.Id);
            var newDebt = outstanding + quantity * batch.UnitPrice * (1 - agent.CommissionPercent / 100m);
            if (agent.CreditLimit > 0 && newDebt > agent.CreditLimit)
            {
                return OperationResult<int>.Fail($"This sale would take {agent.Name} over their credit limit of {agent.CreditLimit:#,0}. Collect cash first.");
            }
        }

        var vouchers = await db.Vouchers
            .FromSqlInterpolated($"SELECT *, xmin FROM vouchers WHERE batch_id = {batchId} AND status = 0 ORDER BY code LIMIT {quantity} FOR UPDATE SKIP LOCKED")
            .ToListAsync();
        if (vouchers.Count < quantity)
        {
            return OperationResult<int>.Fail($"Only {vouchers.Count} unsold vouchers left in this batch.");
        }

        foreach (var voucher in vouchers)
        {
            voucher.Status = VoucherStatus.Sold;
            voucher.SoldAt = now;
            voucher.AgentId = sellingAgentId;
            AddSaleLedger(db, batch, voucher, agent, now, currentUser.Email);
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        await audit.WriteAsync("voucher.sold", "VoucherBatch", batch.Id.ToString(),
            $"{quantity} sold{(agent == null ? "" : $" by {agent.Name}")}");
        return OperationResult<int>.Ok(vouchers.Count);
    }

    public async Task<OperationResult<bool>> CancelAsync(Guid voucherId, string reason)
    {
        Demand(Permissions.ManageVouchers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var voucher = await db.Vouchers.FirstOrDefaultAsync(v => v.Id == voucherId);
        if (voucher == null || voucher.Status is VoucherStatus.Cancelled or VoucherStatus.Expired)
        {
            return OperationResult<bool>.Fail("This voucher cannot be cancelled.");
        }

        await CancelVoucherAsync(db, voucher, reason, now);
        await db.SaveChangesAsync();
        await audit.WriteAsync("voucher.cancelled", "Voucher", voucher.Id.ToString(), $"{voucher.Code}: {reason}");
        return OperationResult<bool>.Ok(true);
    }

    public async Task<OperationResult<int>> CancelUnsoldInBatchAsync(Guid batchId, string reason)
    {
        Demand(Permissions.ManageVouchers);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = kiboDb.ForCurrentUser();
        var vouchers = await db.Vouchers.Where(v => v.BatchId == batchId && v.Status == VoucherStatus.Available).ToListAsync();
        foreach (var voucher in vouchers)
        {
            await CancelVoucherAsync(db, voucher, reason, now);
        }

        await db.SaveChangesAsync();
        await audit.WriteAsync("voucher_batch.cancelled", "VoucherBatch", batchId.ToString(), $"{vouchers.Count} unsold: {reason}");
        return OperationResult<int>.Ok(vouchers.Count);
    }

    private static async Task CancelVoucherAsync(AppDbContext db, Voucher voucher, string reason, DateTime now)
    {
        voucher.Status = VoucherStatus.Cancelled;
        voucher.CancelledAt = now;
        voucher.CancelReason = reason;
        var account = await db.AccessAccounts.FirstAsync(a => a.Id == voucher.AccessAccountId);
        account.Status = AccessAccountStatus.Disabled;
        var subscription = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == voucher.SubscriptionId);
        if (subscription != null && subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Unused)
        {
            subscription.Status = SubscriptionStatus.Cancelled;
            subscription.EndedAt = now;
            subscription.EndReason = $"Voucher cancelled: {reason}";
        }

        await db.SaveChangesAsync();
        await RadiusProvisioner.SyncAccountAsync(db, account.Id, now);
        JobQueue.Enqueue(db, JobKinds.DisconnectUser, new DisconnectUserPayload(voucher.TenantId, voucher.Code, "Voucher cancelled"),
            now, voucher.TenantId);
    }

    public static void AddSaleLedger(AppDbContext db, VoucherBatch batch, Voucher voucher, Agent? agent, DateTime now, string by)
    {
        db.LedgerEntries.Add(new LedgerEntry
        {
            TenantId = voucher.TenantId,
            Kind = LedgerEntryKind.Sale,
            Amount = batch.UnitPrice,
            Currency = "UGX",
            VoucherId = voucher.Id,
            AgentId = agent?.Id,
            SiteId = batch.SiteId,
            SubscriptionId = voucher.SubscriptionId,
            Description = $"Voucher {voucher.Code} ({batch.Name})",
            CreatedBy = by,
            CreatedAt = now
        });

        if (agent != null && agent.CommissionPercent > 0)
        {
            db.LedgerEntries.Add(new LedgerEntry
            {
                TenantId = voucher.TenantId,
                Kind = LedgerEntryKind.AgentCommission,
                Amount = Math.Round(batch.UnitPrice * agent.CommissionPercent / 100m, 2),
                Currency = "UGX",
                VoucherId = voucher.Id,
                AgentId = agent.Id,
                SiteId = batch.SiteId,
                Description = $"{agent.CommissionPercent:0.##}% commission on {voucher.Code}",
                CreatedBy = by,
                CreatedAt = now
            });
        }
    }

    public static async Task<decimal> AgentBalanceAsync(AppDbContext db, Guid agentId)
    {
        var entries = await db.LedgerEntries.Where(l => l.AgentId == agentId)
            .GroupBy(l => l.Kind).Select(g => new { g.Key, Total = g.Sum(l => l.Amount) }).ToListAsync();
        decimal Total(LedgerEntryKind kind) => entries.FirstOrDefault(e => e.Key == kind)?.Total ?? 0;
        return Total(LedgerEntryKind.Sale) - Total(LedgerEntryKind.AgentCommission) - Total(LedgerEntryKind.CashCollected);
    }

    private void Demand(params string[] anyOf)
    {
        if (!anyOf.Any(currentUser.Can))
        {
            throw new UnauthorizedAccessException("You do not have access to vouchers.");
        }
    }
}
