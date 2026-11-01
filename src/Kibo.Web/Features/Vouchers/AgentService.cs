using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Vouchers;

public class AgentForm
{
    [Required, StringLength(80)]
    public string Name { get; set; } = "";

    [Required, StringLength(20)]
    public string PhoneNumber { get; set; } = "";

    public Guid? SiteId { get; set; }

    [Range(0, 50)]
    public decimal CommissionPercent { get; set; } = 10;

    [Range(0, 100_000_000)]
    public decimal CreditLimit { get; set; } = 100_000;
}

public record AgentSummary(Agent Agent, int VouchersSold, decimal Sales, decimal Commission, decimal CashCollected, decimal Outstanding);

public class AgentService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public async Task<List<AgentSummary>> ListAsync()
    {
        Demand(Permissions.ManageVouchers);
        await using var db = kiboDb.ForCurrentUser();
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
        var totals = await db.LedgerEntries.AsNoTracking().Where(l => l.AgentId != null)
            .GroupBy(l => new { l.AgentId, l.Kind })
            .Select(g => new { g.Key.AgentId, g.Key.Kind, Total = g.Sum(l => l.Amount), Count = g.Count() })
            .ToListAsync();

        return agents.Select(a =>
        {
            var mine = totals.Where(t => t.AgentId == a.Id).ToList();
            decimal Sum(LedgerEntryKind kind) => mine.Where(t => t.Kind == kind).Sum(t => t.Total);
            var sales = Sum(LedgerEntryKind.Sale);
            var commission = Sum(LedgerEntryKind.AgentCommission);
            var cash = Sum(LedgerEntryKind.CashCollected);
            return new AgentSummary(a, mine.Where(t => t.Kind == LedgerEntryKind.Sale).Sum(t => t.Count), sales, commission, cash,
                sales - commission - cash);
        }).ToList();
    }

    public async Task<OperationResult<Agent>> CreateAsync(AgentForm form)
    {
        Demand(Permissions.ManageVouchers);
        var phone = PhoneNumbers.NormalizeUganda(form.PhoneNumber);
        if (phone == null)
        {
            return OperationResult<Agent>.Fail("Enter a valid phone number for the agent.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantScope);
        if (!tenant.AgentsEnabled)
        {
            return OperationResult<Agent>.Fail("Agents are not included in your plan.");
        }

        var agent = new Agent
        {
            Name = form.Name.Trim(),
            PhoneNumber = phone,
            SiteId = form.SiteId,
            CommissionPercent = form.CommissionPercent,
            CreditLimit = form.CreditLimit,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        await audit.WriteAsync("agent.created", "Agent", agent.Id.ToString(), $"{agent.Name}, {agent.CommissionPercent}%");
        return OperationResult<Agent>.Ok(agent);
    }

    public async Task<OperationResult<decimal>> RecordCashCollectionAsync(Guid agentId, decimal amount, string? note)
    {
        Demand(Permissions.ManageFinance);
        if (amount <= 0)
        {
            return OperationResult<decimal>.Fail("Enter the amount of cash received.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null)
        {
            return OperationResult<decimal>.Fail("Agent not found.");
        }

        var outstanding = await VoucherService.AgentBalanceAsync(db, agentId);
        if (amount > outstanding)
        {
            return OperationResult<decimal>.Fail($"{agent.Name} only owes {outstanding:#,0}. Record the exact amount received.");
        }

        db.LedgerEntries.Add(new LedgerEntry
        {
            TenantId = agent.TenantId,
            Kind = LedgerEntryKind.CashCollected,
            Amount = amount,
            Currency = "UGX",
            AgentId = agent.Id,
            SiteId = agent.SiteId,
            Description = $"Cash received from {agent.Name}{(string.IsNullOrWhiteSpace(note) ? "" : $": {note.Trim()}")}",
            CreatedBy = currentUser.Email,
            CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync();
        await audit.WriteAsync("agent.cash_collected", "Agent", agent.Id.ToString(), $"{amount:#,0}");
        return OperationResult<decimal>.Ok(outstanding - amount);
    }

    public async Task SetActiveAsync(Guid agentId, bool active)
    {
        Demand(Permissions.ManageVouchers);
        await using var db = kiboDb.ForCurrentUser();
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent == null)
        {
            return;
        }

        agent.IsActive = active;
        await db.SaveChangesAsync();
        await audit.WriteAsync(active ? "agent.activated" : "agent.deactivated", "Agent", agentId.ToString(), agent.Name);
    }

    private void Demand(string permission)
    {
        if (!currentUser.Can(permission))
        {
            throw new UnauthorizedAccessException($"Missing permission {permission}.");
        }
    }
}
