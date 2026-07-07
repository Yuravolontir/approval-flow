namespace ApprovalFlow.Shared.Models;

public class WorkflowState
{
    public string InvoiceId { get; set; } = string.Empty;
    public InvoiceDto Invoice { get; set; } = new();
    public AgentDecision? AgentDecision { get; set; }
    public RouteDecision Route { get; set; }
    public string RouteReason { get; set; } = string.Empty;
    public List<string> RouterViolations { get; set; } = new();
    public SagaStep CurrentSagaStep { get; set; } = SagaStep.NotStarted;
    public HitlStatus HitlStatus { get; set; } = HitlStatus.NotRequired;
    public string? HitlDecision { get; set; } // "approve", "reject", "request_info"
    public string? ReservationId { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Received;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum SagaStep
{
    NotStarted,
    BudgetReserved,
    PaymentExecuted,
    StatusUpdated,
    Compensating,
    Compensated,
    Failed,
    // Appended (not inserted before BudgetReserved) so existing int-serialized states
    // keep stable numeric values. Marks "AddInFlight done, reserve may be in flight,
    // BudgetReserved not yet persisted" so recovery can resume an orphaned reservation.
    Reserving
}

public enum HitlStatus
{
    NotRequired,
    PendingReview,
    Approved,
    Rejected,
    InfoRequested
}

public enum InvoiceStatus
{
    Received,
    Processing,
    AutoApproved,
    PendingReview,
    Approved,
    Rejected,
    Paid,
    PaymentFailed,
    Duplicate
}
