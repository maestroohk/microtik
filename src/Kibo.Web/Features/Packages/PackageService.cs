using System.ComponentModel.DataAnnotations;
using Kibo.Web.Data;
using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Platform;
using Kibo.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Kibo.Web.Features.Packages;

public class PackageForm : IValidatableObject
{
    [Required, StringLength(60)]
    public string Name { get; set; } = "";

    [StringLength(200)]
    public string Description { get; set; } = "";

    public ServiceType ServiceType { get; set; } = ServiceType.Hotspot;

    [Range(0, 100_000_000)]
    public decimal Price { get; set; }

    [Range(1, 1000)]
    public int ValidityAmount { get; set; } = 1;

    public ValidityUnit ValidityUnit { get; set; } = ValidityUnit.Days;
    public ValidityStart ValidityStart { get; set; } = ValidityStart.OnFirstLogin;

    [Range(1, 365)]
    public int ActivationDeadlineDays { get; set; } = 30;

    [Range(1, 1_000_000)]
    public int? TimeQuotaMinutes { get; set; }

    [Range(1, 10_000_000)]
    public long? DataQuotaMegabytes { get; set; }

    [Range(1, 100_000)]
    public int? MaxSessionMinutes { get; set; }

    [Range(0, 10_000_000)]
    public int DownloadKbps { get; set; } = 2048;

    [Range(0, 10_000_000)]
    public int UploadKbps { get; set; } = 1024;

    [Range(1, 10_000_000)]
    public int? ThrottledDownloadKbps { get; set; }

    [Range(1, 10_000_000)]
    public int? ThrottledUploadKbps { get; set; }

    [Range(1, 20)]
    public int MaxDevices { get; set; } = 1;

    public TimeOnly? AccessWindowStart { get; set; }
    public TimeOnly? AccessWindowEnd { get; set; }

    [Range(0, 10_080)]
    public int GraceMinutes { get; set; }

    public FairUseEnforcement FairUseEnforcement { get; set; } = FairUseEnforcement.RateChange;

    [Range(15, 90)]
    public int AccountingIntervalSeconds { get; set; } = 60;

    public bool IsRecurring { get; set; }
    public bool IsPromotional { get; set; }
    public DateTime? AvailableFrom { get; set; }
    public DateTime? AvailableUntil { get; set; }
    public bool ShowOnPortal { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public Guid? SiteId { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((AccessWindowStart == null) != (AccessWindowEnd == null))
        {
            yield return new ValidationResult("Set both the start and end of the access window, or neither.",
                [nameof(AccessWindowStart)]);
        }

        if (AccessWindowStart != null && AccessWindowStart == AccessWindowEnd)
        {
            yield return new ValidationResult("The access window cannot start and end at the same time.",
                [nameof(AccessWindowEnd)]);
        }

        if ((ThrottledDownloadKbps != null || ThrottledUploadKbps != null) && DataQuotaMegabytes == null)
        {
            yield return new ValidationResult("A fair-usage speed only applies to packages with a data allowance.",
                [nameof(ThrottledDownloadKbps)]);
        }

        if (AvailableFrom != null && AvailableUntil != null && AvailableUntil <= AvailableFrom)
        {
            yield return new ValidationResult("The promotion must end after it starts.", [nameof(AvailableUntil)]);
        }
    }

    public static PackageForm From(Package p) => new()
    {
        Name = p.Name, Description = p.Description, ServiceType = p.ServiceType, Price = p.Price,
        ValidityAmount = p.ValidityAmount, ValidityUnit = p.ValidityUnit, ValidityStart = p.ValidityStart,
        ActivationDeadlineDays = p.ActivationDeadlineDays, TimeQuotaMinutes = p.TimeQuotaMinutes,
        DataQuotaMegabytes = p.DataQuotaMegabytes, MaxSessionMinutes = p.MaxSessionMinutes,
        DownloadKbps = p.DownloadKbps, UploadKbps = p.UploadKbps, ThrottledDownloadKbps = p.ThrottledDownloadKbps,
        ThrottledUploadKbps = p.ThrottledUploadKbps, MaxDevices = p.MaxDevices, AccessWindowStart = p.AccessWindowStart,
        AccessWindowEnd = p.AccessWindowEnd, GraceMinutes = p.GraceMinutes, IsRecurring = p.IsRecurring,
        FairUseEnforcement = p.FairUseEnforcement, AccountingIntervalSeconds = p.AccountingIntervalSeconds,
        IsPromotional = p.IsPromotional, AvailableFrom = p.AvailableFrom, AvailableUntil = p.AvailableUntil,
        ShowOnPortal = p.ShowOnPortal, IsActive = p.IsActive, SortOrder = p.SortOrder, SiteId = p.SiteId
    };

    private static DateTime? ToUtc(DateTime? local)
    {
        if (local == null || local.Value.Kind == DateTimeKind.Utc)
        {
            return local;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Kampala");
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified), zone);
    }

    public void ApplyTo(Package p)
    {
        p.Name = Name.Trim(); p.Description = Description.Trim(); p.ServiceType = ServiceType; p.Price = Price;
        p.ValidityAmount = ValidityAmount; p.ValidityUnit = ValidityUnit; p.ValidityStart = ValidityStart;
        p.ActivationDeadlineDays = ActivationDeadlineDays; p.TimeQuotaMinutes = TimeQuotaMinutes;
        p.DataQuotaMegabytes = DataQuotaMegabytes; p.MaxSessionMinutes = MaxSessionMinutes;
        p.DownloadKbps = DownloadKbps; p.UploadKbps = UploadKbps; p.ThrottledDownloadKbps = ThrottledDownloadKbps;
        p.ThrottledUploadKbps = ThrottledUploadKbps; p.MaxDevices = MaxDevices; p.AccessWindowStart = AccessWindowStart;
        p.AccessWindowEnd = AccessWindowEnd; p.GraceMinutes = GraceMinutes; p.IsRecurring = IsRecurring;
        p.FairUseEnforcement = FairUseEnforcement; p.AccountingIntervalSeconds = AccountingIntervalSeconds;
        p.IsPromotional = IsPromotional; p.AvailableFrom = ToUtc(AvailableFrom); p.AvailableUntil = ToUtc(AvailableUntil);
        p.ShowOnPortal = ShowOnPortal; p.IsActive = IsActive; p.SortOrder = SortOrder; p.SiteId = SiteId;
    }
}

public static class PackageCatalog
{
    public static bool IsAvailable(Package package, DateTime now) =>
        package.IsActive &&
        (package.AvailableFrom == null || package.AvailableFrom <= now) &&
        (package.AvailableUntil == null || package.AvailableUntil > now);

    public static string DescribeValidity(int amount, ValidityUnit unit) => unit switch
    {
        ValidityUnit.Minutes => amount == 1 ? "1 minute" : $"{amount} minutes",
        ValidityUnit.Hours => amount == 1 ? "1 hour" : $"{amount} hours",
        ValidityUnit.Days => amount == 1 ? "24 hours" : amount == 7 ? "1 week" : $"{amount} days",
        ValidityUnit.CalendarMonths => amount == 1 ? "1 month" : $"{amount} months",
        _ => ""
    };

    public static string DescribeData(long? megabytes) => megabytes switch
    {
        null => "Unlimited data",
        >= 1024 when megabytes % 1024 == 0 => $"{megabytes / 1024} GB",
        >= 1024 => $"{megabytes / 1024.0:0.#} GB",
        _ => $"{megabytes} MB"
    };

    public static string DescribeSpeed(int kbps) => kbps switch
    {
        <= 0 => "Uncapped",
        >= 1024 when kbps % 1024 == 0 => $"{kbps / 1024} Mbps",
        >= 1024 => $"{kbps / 1024.0:0.#} Mbps",
        _ => $"{kbps} kbps"
    };
}

public class PackageService(KiboDb kiboDb, CurrentUser currentUser, AuditLogger audit, TimeProvider clock)
{
    public async Task<List<Package>> ListAsync(bool includeInactive = true)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Packages.AsNoTracking()
            .Where(p => includeInactive || p.IsActive)
            .OrderBy(p => p.ServiceType).ThenBy(p => p.SortOrder).ThenBy(p => p.Price)
            .ToListAsync();
    }

    public async Task<Package?> GetAsync(Guid id)
    {
        await using var db = kiboDb.ForCurrentUser();
        return await db.Packages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
    }

    public async Task<OperationResult<Package>> SaveAsync(Guid? id, PackageForm form)
    {
        if (!currentUser.Can(Permissions.ManagePackages))
        {
            throw new UnauthorizedAccessException("Missing permission to manage packages.");
        }

        await using var db = kiboDb.ForCurrentUser();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == db.TenantScope);
        if (form.ServiceType == ServiceType.Pppoe && !tenant.PppoeEnabled)
        {
            return OperationResult<Package>.Fail("PPPoE is not enabled for your subscription plan.");
        }

        Package package;
        if (id == null)
        {
            package = new Package { CreatedAt = clock.GetUtcNow().UtcDateTime, Currency = tenant.Currency };
            db.Packages.Add(package);
        }
        else
        {
            package = await db.Packages.FirstOrDefaultAsync(p => p.Id == id) ?? throw new InvalidOperationException("Package not found.");
        }

        form.ApplyTo(package);
        await db.SaveChangesAsync();
        await audit.WriteAsync(id == null ? "package.created" : "package.updated", "Package", package.Id.ToString(),
            $"{package.Name} at {package.Price} {package.Currency}");
        return OperationResult<Package>.Ok(package);
    }
}
