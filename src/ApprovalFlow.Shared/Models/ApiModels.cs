namespace ApprovalFlow.Shared.Models;

public class SubmitInvoiceResponse
{
    public string TrackingId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? OriginalId { get; set; }
}

public class InvoiceStatusResponse
{
    public string InvoiceId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public RouteDecision? Route { get; set; }
    public AgentDecision? AgentDecision { get; set; }
}

public class HitlDecisionRequest
{
    public string Action { get; set; } = string.Empty; // "approve", "reject", "request_info"
    public string? Comment { get; set; }
}

public class HitlQueueItem
{
    public string InvoiceId { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public AgentDecision? AgentDecision { get; set; }
    public List<string> RouterViolations { get; set; } = new();
    public string RouteReason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class BudgetReserveRequest
{
    public string Department { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string InvoiceId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class BudgetReserveResponse
{
    public string ReservationId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class PaymentExecuteRequest
{
    public string InvoiceId { get; set; } = string.Empty;
    public string ReservationId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Scenario { get; set; }
}

public class PaymentExecuteResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class DashboardResponse
{
    public int TotalProcessed { get; set; }
    public int AutoApproved { get; set; }
    public int HumanReviewed { get; set; }
    public int Rejected { get; set; }
    public int Duplicates { get; set; }
    public decimal TotalAutoApprovedAmount { get; set; }
    public decimal TotalHumanReviewedAmount { get; set; }
}
