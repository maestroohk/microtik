using Kibo.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, Guid? tenantScope) : base(options)
    {
        TenantScope = tenantScope;
    }

    public Guid? TenantScope { get; }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Router> Routers => Set<Router>();
    public DbSet<RouterHealthSample> RouterHealthSamples => Set<RouterHealthSample>();
    public DbSet<RouterEvent> RouterEvents => Set<RouterEvent>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerDevice> CustomerDevices => Set<CustomerDevice>();
    public DbSet<SupportNote> SupportNotes => Set<SupportNote>();
    public DbSet<AccessAccount> AccessAccounts => Set<AccessAccount>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentCallback> PaymentCallbacks => Set<PaymentCallback>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<VoucherBatch> VoucherBatches => Set<VoucherBatch>();
    public DbSet<Voucher> Vouchers => Set<Voucher>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();
    public DbSet<RadiusAccess> RadiusAccess => Set<RadiusAccess>();
    public DbSet<RadiusNas> RadiusNas => Set<RadiusNas>();
    public DbSet<RadiusAccounting> RadiusAccounting => Set<RadiusAccounting>();
    public DbSet<RadiusPostAuth> RadiusPostAuth => Set<RadiusPostAuth>();
    public DbSet<RadiusAccountingEvent> RadiusAccountingEvents => Set<RadiusAccountingEvent>();
    public DbSet<EnforcementAction> EnforcementActions => Set<EnforcementAction>();
    public DbSet<ProvisioningOperation> ProvisioningOperations => Set<ProvisioningOperation>();
    public DbSet<RouterBackup> RouterBackups => Set<RouterBackup>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(120);
            e.Property(t => t.Slug).HasMaxLength(60);
        });

        model.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).HasMaxLength(200);
        });

        model.Entity<AuditLog>(e => e.HasIndex(a => new { a.TenantId, a.CreatedAt }));

        model.Entity<Site>(e =>
        {
            e.HasIndex(s => s.Slug).IsUnique();
            e.Property(s => s.Slug).HasMaxLength(60);
        });

        model.Entity<Router>(e =>
        {
            e.HasIndex(r => new { r.TenantId, r.Name }).IsUnique();
            e.HasIndex(r => r.NasIpAddress).IsUnique();
            e.HasIndex(r => r.HardwareId).IsUnique().HasFilter("hardware_id IS NOT NULL");
        });

        model.Entity<RouterHealthSample>(e => e.HasIndex(s => new { s.RouterId, s.TakenAt }));
        model.Entity<RouterEvent>(e => e.HasIndex(s => new { s.RouterId, s.OccurredAt }));
        model.Entity<EnforcementAction>(e => e.HasIndex(a => new { a.TenantId, a.CreatedAt }));

        model.Entity<Customer>(e => e.HasIndex(c => new { c.TenantId, c.PhoneNumber }).IsUnique());
        model.Entity<CustomerDevice>(e => e.HasIndex(d => new { d.TenantId, d.MacAddress }));
        model.Entity<AccessAccount>(e => e.HasIndex(a => new { a.TenantId, a.Username }).IsUnique());

        model.Entity<Package>(e => e.Property(p => p.Price).HasPrecision(14, 2));

        model.Entity<Subscription>(e =>
        {
            e.Property(s => s.PricePaid).HasPrecision(14, 2);
            e.Property(s => s.Version).IsRowVersion();
            e.HasIndex(s => new { s.AccessAccountId, s.Status });
            e.HasIndex(s => new { s.TenantId, s.Source, s.SourceReference }).IsUnique()
                .HasFilter("source_reference IS NOT NULL");
        });

        model.Entity<Payment>(e =>
        {
            e.Property(p => p.Amount).HasPrecision(14, 2);
            e.Property(p => p.Version).IsRowVersion();
            e.HasIndex(p => p.Reference).IsUnique();
            e.HasIndex(p => p.PortalToken).IsUnique();
            e.HasIndex(p => new { p.Provider, p.ProviderReference }).IsUnique()
                .HasFilter("provider_reference IS NOT NULL");
            e.HasIndex(p => new { p.Status, p.CreatedAt });
        });

        model.Entity<LedgerEntry>(e =>
        {
            e.Property(l => l.Amount).HasPrecision(14, 2);
            e.HasIndex(l => new { l.TenantId, l.CreatedAt });
            e.HasIndex(l => new { l.PaymentId, l.Kind }).IsUnique().HasFilter("payment_id IS NOT NULL");
        });

        model.Entity<VoucherBatch>(e => e.Property(b => b.UnitPrice).HasPrecision(14, 2));
        model.Entity<Voucher>(e =>
        {
            e.HasIndex(v => new { v.TenantId, v.Code }).IsUnique();
            e.Property(v => v.Version).IsRowVersion();
        });
        model.Entity<Agent>(e =>
        {
            e.Property(a => a.CommissionPercent).HasPrecision(5, 2);
            e.Property(a => a.CreditLimit).HasPrecision(14, 2);
        });

        model.Entity<BackgroundJob>(e =>
        {
            e.HasIndex(j => new { j.Status, j.RunAfter });
            e.HasIndex(j => j.DeduplicationKey).IsUnique()
                .HasFilter("deduplication_key IS NOT NULL AND status IN (0, 1)");
        });

        model.Entity<RadiusAccess>(e =>
        {
            e.ToTable("access", "radius");
            e.HasKey(a => new { a.TenantId, a.Username });
            e.HasIndex(a => a.AccessAccountId).IsUnique();
        });

        model.Entity<RadiusNas>(e =>
        {
            e.ToTable("nas", "radius");
            e.HasIndex(n => n.NasName).IsUnique();
        });

        model.Entity<RadiusAccounting>(e =>
        {
            e.ToTable("radacct", "radius");
            e.HasKey(a => a.RadAcctId);
            e.Property(a => a.RadAcctId).HasColumnName("radacctid");
            e.Property(a => a.AcctSessionId).HasColumnName("acctsessionid");
            e.Property(a => a.AcctUniqueId).HasColumnName("acctuniqueid");
            e.Property(a => a.UserName).HasColumnName("username");
            e.Property(a => a.NasIpAddress).HasColumnName("nasipaddress");
            e.Property(a => a.NasPortId).HasColumnName("nasportid");
            e.Property(a => a.AcctStartTime).HasColumnName("acctstarttime");
            e.Property(a => a.AcctUpdateTime).HasColumnName("acctupdatetime");
            e.Property(a => a.AcctStopTime).HasColumnName("acctstoptime");
            e.Property(a => a.AcctSessionTime).HasColumnName("acctsessiontime");
            e.Property(a => a.AcctInputOctets).HasColumnName("acctinputoctets");
            e.Property(a => a.AcctOutputOctets).HasColumnName("acctoutputoctets");
            e.Property(a => a.CalledStationId).HasColumnName("calledstationid");
            e.Property(a => a.CallingStationId).HasColumnName("callingstationid");
            e.Property(a => a.AcctTerminateCause).HasColumnName("acctterminatecause");
            e.Property(a => a.FramedIpAddress).HasColumnName("framedipaddress");
            e.Property(a => a.Class).HasColumnName("class");
            e.Property(a => a.TenantId).HasColumnName("tenant_id");
            e.Property(a => a.RouterId).HasColumnName("router_id");
            e.Property(a => a.SubscriptionId).HasColumnName("subscription_id");
            e.Property(a => a.NasEventTime).HasColumnName("nas_event_time");
            e.Property(a => a.UpdateCount).HasColumnName("update_count");
            e.HasIndex(a => a.AcctUniqueId);
            e.HasIndex(a => new { a.NasIpAddress, a.AcctSessionId, a.UserName });
            e.HasIndex(a => new { a.SubscriptionId, a.AcctStopTime });
            e.HasIndex(a => new { a.UserName, a.AcctStopTime });
            e.HasIndex(a => new { a.NasIpAddress, a.AcctStopTime });
        });

        model.Entity<RadiusAccountingEvent>(e =>
        {
            e.ToTable("acct_events", "radius");
            e.HasIndex(x => x.ReceivedAt);
            e.HasIndex(x => new { x.NasIpAddress, x.SessionId });
        });

        model.Entity<RadiusPostAuth>(e =>
        {
            e.ToTable("radpostauth", "radius");
            e.Property(p => p.UserName).HasColumnName("username");
            e.Property(p => p.Reply).HasColumnName("reply");
            e.Property(p => p.CallingStationId).HasColumnName("callingstationid");
            e.Property(p => p.NasIpAddress).HasColumnName("nasipaddress");
            e.Property(p => p.AuthDate).HasColumnName("authdate");
            e.HasIndex(p => p.AuthDate);
        });

        ApplyTenantFilters(model);
    }

    private void ApplyTenantFilters(ModelBuilder model)
    {
        model.Entity<Site>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Router>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<RouterHealthSample>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<RouterEvent>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<EnforcementAction>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<ProvisioningOperation>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<RouterBackup>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Customer>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<CustomerDevice>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<SupportNote>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<AccessAccount>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Package>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Subscription>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Payment>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<LedgerEntry>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<VoucherBatch>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Voucher>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<Agent>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<User>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<AuditLog>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<RadiusAccess>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
        model.Entity<RadiusNas>().HasQueryFilter(e => TenantScope == null || e.TenantId == TenantScope);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        GuardTenantOwnership();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        GuardTenantOwnership();
        return base.SaveChanges();
    }

    private void GuardTenantOwnership()
    {
        foreach (var entry in ChangeTracker.Entries<RadiusNas>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.TenantId == Guid.Empty || (TenantScope != null && entry.Entity.TenantId != TenantScope))
            {
                throw new InvalidOperationException("A RADIUS client must belong to the tenant that owns the router.");
            }
        }

        foreach (var entry in ChangeTracker.Entries<RadiusAccess>().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.Entity.TenantId == Guid.Empty || (TenantScope != null && entry.Entity.TenantId != TenantScope))
            {
                throw new InvalidOperationException("RADIUS access must belong to the tenant that owns the account.");
            }
        }

        if (TenantScope == null)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = TenantScope.Value;
            }

            if (entry.State != EntityState.Unchanged && entry.Entity.TenantId != TenantScope.Value)
            {
                throw new InvalidOperationException(
                    $"Attempted to write {entry.Entity.GetType().Name} belonging to another tenant.");
            }
        }
    }
}
