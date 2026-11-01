using Kibo.Web.Data.Entities;
using Kibo.Web.Features.Customers;
using Kibo.Web.Features.Routers;
using Kibo.Web.Features.Subscriptions;

namespace Kibo.Tests;

public class EntitlementTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Package DailyPackage(ValidityStart start = ValidityStart.OnFirstLogin) => new()
    {
        TenantId = Guid.NewGuid(),
        Name = "Daily 1GB",
        Price = 1000,
        ValidityAmount = 1,
        ValidityUnit = ValidityUnit.Days,
        ValidityStart = start,
        ActivationDeadlineDays = 30,
        DataQuotaMegabytes = 1024,
        DownloadKbps = 4096,
        UploadKbps = 2048,
        MaxDevices = 2
    };

    private static AccessAccount Account(Package package) => new()
    {
        TenantId = package.TenantId,
        Username = "256772000111",
        Password = "123456",
        Kind = AccessAccountKind.Hotspot
    };

    [Fact]
    public void First_login_package_does_not_start_its_clock_at_purchase()
    {
        var package = DailyPackage();
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        Assert.Equal(SubscriptionStatus.Unused, subscription.Status);
        Assert.Null(subscription.ExpiresAt);
        Assert.Equal(Now.AddDays(30), subscription.ActivateBy);
    }

    [Fact]
    public void Purchase_time_package_expires_relative_to_purchase()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Now.AddDays(1), subscription.ExpiresAt);
    }

    [Fact]
    public void Purchase_while_another_package_is_current_is_queued_not_merged()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R2", true, 1000);

        Assert.Equal(SubscriptionStatus.Scheduled, subscription.Status);
        Assert.Null(subscription.ExpiresAt);
        Assert.Null(subscription.CountingSince);
    }

    [Fact]
    public void First_session_activates_using_the_session_start_not_the_evaluation_time()
    {
        var package = DailyPackage();
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);
        var firstSession = Now.AddHours(5);

        var decision = Entitlements.Evaluate(subscription, new UsageTotals(0, 0, firstSession), Now.AddHours(6));
        Assert.Equal(SubscriptionStatus.Active, decision.Status);

        Entitlements.Activate(subscription, firstSession);
        Assert.Equal(firstSession.AddDays(1), subscription.ExpiresAt);
    }

    [Fact]
    public void Unused_package_expires_after_activation_deadline()
    {
        var package = DailyPackage();
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Voucher, "V1", false, 1000);

        var decision = Entitlements.Evaluate(subscription, new UsageTotals(0, 0, null), Now.AddDays(31));

        Assert.Equal(SubscriptionStatus.Expired, decision.Status);
    }

    [Fact]
    public void Data_quota_ends_package_when_there_is_no_fair_use_speed()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        var decision = Entitlements.Evaluate(subscription, new UsageTotals(1024L * 1024 * 1024, 60, Now), Now.AddHours(1));

        Assert.Equal(SubscriptionStatus.Exhausted, decision.Status);
    }

    [Fact]
    public void Data_quota_keeps_package_active_when_fair_use_speed_is_set()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        package.ThrottledDownloadKbps = 512;
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        var decision = Entitlements.Evaluate(subscription, new UsageTotals(2L * 1024 * 1024 * 1024, 60, Now), Now.AddHours(1));

        Assert.Equal(SubscriptionStatus.Active, decision.Status);
    }

    [Fact]
    public void Accumulated_online_time_is_separate_from_calendar_validity()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        package.ValidityAmount = 7;
        package.TimeQuotaMinutes = 300;
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        Assert.Equal(SubscriptionStatus.Active, Entitlements.Evaluate(subscription, new UsageTotals(0, 299 * 60, Now), Now.AddDays(2)).Status);
        Assert.Equal(SubscriptionStatus.Exhausted, Entitlements.Evaluate(subscription, new UsageTotals(0, 300 * 60, Now), Now.AddDays(2)).Status);
    }

    [Fact]
    public void Grace_period_extends_access_beyond_expiry()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        package.GraceMinutes = 60;
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        Assert.Equal(SubscriptionStatus.Active, Entitlements.Evaluate(subscription, new UsageTotals(0, 0, Now), Now.AddDays(1).AddMinutes(30)).Status);
        Assert.Equal(SubscriptionStatus.Expired, Entitlements.Evaluate(subscription, new UsageTotals(0, 0, Now), Now.AddDays(1).AddMinutes(61)).Status);
    }

    [Fact]
    public void Calendar_months_follow_the_calendar()
    {
        var start = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 2, 28, 12, 0, 0, DateTimeKind.Utc), Entitlements.AddValidity(start, 1, ValidityUnit.CalendarMonths));
    }

    [Fact]
    public void Package_changes_after_purchase_do_not_change_the_purchased_terms()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var subscription = Entitlements.CreateFromPackage(package, Account(package), Now, SubscriptionSource.Payment, "R1", false, 1000);

        package.DataQuotaMegabytes = 1;
        package.DownloadKbps = 1;

        Assert.Equal(1024L * 1024 * 1024, subscription.DataQuotaBytes);
        Assert.Equal(4096, subscription.DownloadKbps);
    }

    [Fact]
    public void Radius_access_is_disabled_for_suspended_customers_and_tenants()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var account = Account(package);
        var subscription = Entitlements.CreateFromPackage(package, account, Now, SubscriptionSource.Payment, "R1", false, 1000);
        var tenant = new Tenant { Id = package.TenantId, TimeZone = "Africa/Kampala" };

        Assert.True(Entitlements.BuildRadiusAccess(account, subscription, tenant, new Customer(), Now).Enabled);
        Assert.False(Entitlements.BuildRadiusAccess(account, subscription, tenant, new Customer { Status = CustomerStatus.Suspended }, Now).Enabled);

        tenant.Status = TenantStatus.Suspended;
        Assert.False(Entitlements.BuildRadiusAccess(account, subscription, tenant, null, Now).Enabled);
    }

    [Fact]
    public void Radius_access_carries_mikrotik_rate_limit_in_upload_download_order()
    {
        var package = DailyPackage(ValidityStart.OnPurchase);
        var account = Account(package);
        var subscription = Entitlements.CreateFromPackage(package, account, Now, SubscriptionSource.Payment, "R1", false, 1000);

        var access = Entitlements.BuildRadiusAccess(account, subscription, new Tenant(), null, Now);

        Assert.Equal("2048k/4096k", access.RateLimit);
        Assert.Equal(2, access.SimultaneousUse);
        Assert.Equal(subscription.ExpiresAt, access.ExpiresAt);
    }

    [Theory]
    [InlineData("0772 123456", "256772123456")]
    [InlineData("+256 701-234-567", "256701234567")]
    [InlineData("772123456", "256772123456")]
    [InlineData("12345", null)]
    [InlineData("", null)]
    public void Ugandan_numbers_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.NormalizeUganda(input));

    [Theory]
    [InlineData("1d2h3m4s", 93784)]
    [InlineData("2w", 1209600)]
    [InlineData("45s", 45)]
    [InlineData("1d00:01:05", 86465)]
    [InlineData("", 0)]
    public void RouterOs_durations_are_parsed(string value, long seconds) =>
        Assert.Equal(seconds, RouterOsClient.ParseDuration(value));

    [Fact]
    public void Router_check_backoff_is_bounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), RouterHealthChecker.NextInterval(0));
        Assert.Equal(TimeSpan.FromSeconds(30), RouterHealthChecker.NextInterval(1));
        Assert.Equal(TimeSpan.FromMinutes(15), RouterHealthChecker.NextInterval(50));
    }
}
