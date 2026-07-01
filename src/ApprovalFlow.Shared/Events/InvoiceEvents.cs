using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Shared.Events;

public class InvoiceSubmittedEvent
{
    public string InvoiceId { get; set; } = string.Empty;
    public InvoiceDto Invoice { get; set; } = new();
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class InvoiceProcessedEvent
{
    public string InvoiceId { get; set; } = string.Empty;
    public RouteDecision Route { get; set; }
    public InvoiceStatus FinalStatus { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
