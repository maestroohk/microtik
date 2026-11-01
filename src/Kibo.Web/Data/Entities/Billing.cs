namespace Kibo.Web.Data.Entities;

public enum PaymentStatus
{
    Created,
    Pending,
    Succeeded,
    Failed,
    Expired,
    Refunded
}

public enum PaymentMethod
{
    MobileMoney,
    Cash,
    BankTransfer,
    Card
}

public class Payment : TenantEntity
{
    public string Reference { get; set; } = "";
    public Guid? SiteId { get; set; }
    public Guid PackageId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid? AccessAccountId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public PaymentMethod Method { get; set; }
    public string Provider { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string PhoneNumber { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "UGX";
    public PaymentStatus Status { get; set; } = PaymentStatus.Created;
    public string? FailureReason { get; set; }
    public string? ClientMacAddress { get; set; }
    public string? ClientIpAddress { get; set; }
    public string PortalToken { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public int VerificationAttempts { get; set; }
    public uint Version { get; set; }
}

public class PaymentCallback
{
    public long Id { get; set; }
    public string Provider { get; set; } = "";
    public string? ProviderReference { get; set; }
    public string? PaymentReference { get; set; }
    public bool SignatureValid { get; set; }
    public string Outcome { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime ReceivedAt { get; set; }
}

public enum LedgerEntryKind
{
    Sale,
    Refund,
    AgentCommission,
    CashCollected,
    Adjustment
}

public class LedgerEntry
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public LedgerEntryKind Kind { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "UGX";
    public Guid? PaymentId { get; set; }
    public Guid? VoucherId { get; set; }
    public Guid? AgentId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string Description { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum VoucherStatus
{
    Available,
    Sold,
    Redeemed,
    Cancelled,
    Expired
}

public class VoucherBatch : TenantEntity
{
    public string Name { get; set; } = "";
    public Guid PackageId { get; set; }
    public Package? Package { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? AgentId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime RedeemBy { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class Voucher : TenantEntity
{
    public Guid BatchId { get; set; }
    public VoucherBatch? Batch { get; set; }
    public string Code { get; set; } = "";
    public VoucherStatus Status { get; set; } = VoucherStatus.Available;
    public Guid AccessAccountId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? AgentId { get; set; }
    public DateTime? SoldAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public uint Version { get; set; }
}

public class Agent : TenantEntity
{
    public string Name { get; set; } = "";
    public string PhoneNumber { get; set; } = "";
    public Guid? UserId { get; set; }
    public Guid? SiteId { get; set; }
    public decimal CommissionPercent { get; set; }
    public decimal CreditLimit { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}

public enum JobStatus
{
    Pending,
    Running,
    Succeeded,
    Failed
}

public class BackgroundJob
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public string? DeduplicationKey { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime RunAfter { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
