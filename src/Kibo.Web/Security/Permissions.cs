using Kibo.Web.Data.Entities;

namespace Kibo.Web.Security;

public static class Permissions
{
    public const string ViewDashboard = "dashboard.view";
    public const string ManageRouters = "routers.manage";
    public const string ViewRouters = "routers.view";
    public const string ProvisionRouters = "routers.provision";
    public const string ManageCustomers = "customers.manage";
    public const string ViewCustomers = "customers.view";
    public const string ManagePackages = "packages.manage";
    public const string ManageVouchers = "vouchers.manage";
    public const string SellVouchers = "vouchers.sell";
    public const string ViewFinance = "finance.view";
    public const string ManageFinance = "finance.manage";
    public const string ManageStaff = "staff.manage";
    public const string ManageSettings = "settings.manage";
    public const string ViewAudit = "audit.view";
    public const string DisconnectSessions = "sessions.disconnect";

    private static readonly string[] All =
    [
        ViewDashboard, ManageRouters, ViewRouters, ProvisionRouters, ManageCustomers, ViewCustomers,
        ManagePackages, ManageVouchers, SellVouchers, ViewFinance, ManageFinance, ManageStaff,
        ManageSettings, ViewAudit, DisconnectSessions
    ];

    private static readonly Dictionary<UserRole, HashSet<string>> RolePermissions = new()
    {
        [UserRole.Owner] = [.. All],
        [UserRole.Administrator] = [.. All.Where(p => p != ManageFinance)],
        [UserRole.NetworkEngineer] =
            [ViewDashboard, ViewRouters, ManageRouters, ProvisionRouters, ViewCustomers, DisconnectSessions],
        [UserRole.Support] =
            [ViewDashboard, ViewRouters, ViewCustomers, ManageCustomers, DisconnectSessions, SellVouchers],
        [UserRole.Finance] = [ViewDashboard, ViewFinance, ManageFinance, ViewCustomers, ViewAudit],
        [UserRole.Agent] = [SellVouchers]
    };

    public static IReadOnlySet<string> For(UserRole role) => RolePermissions[role];
}
