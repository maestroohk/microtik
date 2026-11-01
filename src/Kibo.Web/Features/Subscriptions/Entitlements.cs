using Kibo.Web.Data.Entities;

namespace Kibo.Web.Features.Subscriptions;

public record UsageTotals(long Bytes, long Seconds, DateTime? FirstSessionStart);

public record LifecycleDecision(SubscriptionStatus Status, string? EndReason);

public static class Entitlements
{
    public static DateTime AddValidity(DateTime start, int amount, ValidityUnit unit) => unit switch
    {
        ValidityUnit.Minutes => start.AddMinutes(amount),
        ValidityUnit.Hours => start.AddHours(amount),
        ValidityUnit.Days => start.AddDays(amount),
        ValidityUnit.CalendarMonths => start.AddMonths(amount),
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    public static long ValiditySeconds(DateTime from, int amount, ValidityUnit unit) =>
        (long)(AddValidity(from, amount, unit) - from).TotalSeconds;

    public static Subscription CreateFromPackage(Package package, AccessAccount account, DateTime now,
        SubscriptionSource source, string? sourceReference, bool accountHasCurrentEntitlement, decimal pricePaid)
    {
        var subscription = new Subscription
        {
            TenantId = account.TenantId,
            AccessAccountId = account.Id,
            PackageId = package.Id,
            PackageName = package.Name,
            Source = source,
            SourceReference = sourceReference,
            PricePaid = pricePaid,
            Currency = package.Currency,
            ValidityAmount = package.ValidityAmount,
            ValidityUnit = package.ValidityUnit,
            ValidityStart = package.ValidityStart,
            DataQuotaBytes = package.DataQuotaMegabytes * 1024 * 1024,
            TimeQuotaSeconds = package.TimeQuotaMinutes * 60L,
            MaxSessionSeconds = package.MaxSessionMinutes * 60,
            DownloadKbps = package.DownloadKbps,
            UploadKbps = package.UploadKbps,
            ThrottledDownloadKbps = package.ThrottledDownloadKbps,
            ThrottledUploadKbps = package.ThrottledUploadKbps,
            MaxDevices = Math.Max(1, package.MaxDevices),
            AccessWindowStart = package.AccessWindowStart,
            AccessWindowEnd = package.AccessWindowEnd,
            GraceSeconds = package.GraceMinutes * 60,
            IsRecurring = package.IsRecurring,
            PurchasedAt = now,
            ActivateBy = now.AddDays(Math.Max(1, package.ActivationDeadlineDays))
        };

        if (accountHasCurrentEntitlement)
        {
            subscription.Status = SubscriptionStatus.Scheduled;
        }
        else
        {
            MakeCurrent(subscription, now);
        }

        return subscription;
    }

    public static void MakeCurrent(Subscription subscription, DateTime now)
    {
        subscription.CountingSince = now;
        if (subscription.ValidityStart == ValidityStart.OnPurchase)
        {
            Activate(subscription, now);
        }
        else
        {
            subscription.Status = SubscriptionStatus.Unused;
            if (subscription.ActivateBy < now)
            {
                subscription.ActivateBy = now.AddDays(1);
            }
        }
    }

    public static void Activate(Subscription subscription, DateTime activatedAt)
    {
        subscription.Status = SubscriptionStatus.Active;
        subscription.ActivatedAt = activatedAt;
        subscription.ExpiresAt = AddValidity(activatedAt, subscription.ValidityAmount, subscription.ValidityUnit);
    }

    public static bool HasThrottleFallback(Subscription subscription) =>
        subscription.ThrottledDownloadKbps != null || subscription.ThrottledUploadKbps != null;

    public static LifecycleDecision Evaluate(Subscription subscription, UsageTotals usage, DateTime now)
    {
        if (subscription.Status == SubscriptionStatus.Unused)
        {
            if (usage.FirstSessionStart != null)
            {
                return new LifecycleDecision(SubscriptionStatus.Active, null);
            }

            return subscription.ActivateBy <= now
                ? new LifecycleDecision(SubscriptionStatus.Expired, "Not used before the activation deadline")
                : new LifecycleDecision(SubscriptionStatus.Unused, null);
        }

        if (subscription.Status != SubscriptionStatus.Active)
        {
            return new LifecycleDecision(subscription.Status, subscription.EndReason);
        }

        if (subscription.ExpiresAt != null && subscription.ExpiresAt.Value.AddSeconds(subscription.GraceSeconds) <= now)
        {
            return new LifecycleDecision(SubscriptionStatus.Expired, "Validity period ended");
        }

        if (subscription.TimeQuotaSeconds != null && usage.Seconds >= subscription.TimeQuotaSeconds)
        {
            return new LifecycleDecision(SubscriptionStatus.Exhausted, "Online time allowance used");
        }

        if (subscription.DataQuotaBytes != null && usage.Bytes >= subscription.DataQuotaBytes &&
            !HasThrottleFallback(subscription))
        {
            return new LifecycleDecision(SubscriptionStatus.Exhausted, "Data allowance used");
        }

        return new LifecycleDecision(SubscriptionStatus.Active, null);
    }

    public static bool IsCurrent(SubscriptionStatus status) =>
        status is SubscriptionStatus.Active or SubscriptionStatus.Unused;

    public static string RateLimit(int uploadKbps, int downloadKbps)
    {
        if (uploadKbps <= 0 && downloadKbps <= 0)
        {
            return "";
        }

        return $"{Rate(uploadKbps)}/{Rate(downloadKbps)}";
    }

    private static string Rate(int kbps) => kbps <= 0 ? "0" : $"{kbps}k";

    public static RadiusAccess BuildRadiusAccess(AccessAccount account, Subscription? current, Tenant tenant,
        Customer? customer, DateTime now)
    {
        var access = new RadiusAccess
        {
            TenantId = account.TenantId,
            Username = account.Username,
            AccessAccountId = account.Id,
            Password = account.Password,
            ServiceType = account.Kind == AccessAccountKind.Pppoe ? "ppp" : "hotspot",
            FramedIpAddress = account.StaticIpAddress,
            TimeZone = tenant.TimeZone,
            UpdatedAt = now
        };

        var accountUsable = account.Status == AccessAccountStatus.Active &&
                            tenant.Status == TenantStatus.Active &&
                            customer is null or { Status: CustomerStatus.Active };

        if (current == null || !accountUsable || !IsCurrent(current.Status))
        {
            access.Enabled = false;
            return access;
        }

        access.Enabled = true;
        access.SubscriptionId = current.Id;
        access.CountingSince = current.CountingSince;
        access.ExpiresAt = current.ExpiresAt?.AddSeconds(current.GraceSeconds);
        access.ValiditySeconds = current.ExpiresAt == null
            ? ValiditySeconds(now, current.ValidityAmount, current.ValidityUnit)
            : null;
        access.ActivateBy = current.Status == SubscriptionStatus.Unused ? current.ActivateBy : null;
        access.DataLimitBytes = current.DataQuotaBytes;
        access.TimeLimitSeconds = current.TimeQuotaSeconds;
        access.MaxSessionSeconds = current.MaxSessionSeconds;
        access.RateLimit = RateLimit(current.UploadKbps, current.DownloadKbps);
        access.ThrottledRateLimit = HasThrottleFallback(current)
            ? RateLimit(current.ThrottledUploadKbps ?? current.UploadKbps, current.ThrottledDownloadKbps ?? current.DownloadKbps)
            : null;
        access.SimultaneousUse = current.MaxDevices;
        access.WindowStart = current.AccessWindowStart;
        access.WindowEnd = current.AccessWindowEnd;
        return access;
    }
}
