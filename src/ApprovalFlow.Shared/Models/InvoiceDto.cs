namespace ApprovalFlow.Shared.Models;

public class InvoiceDto
{
    public string Id { get; set; } = string.Empty;
    public string Submitter { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Vendor { get; set; } = string.Empty;
    public bool VendorKnown { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public string Category { get; set; } = string.Empty;
    public int? Attendees { get; set; }
    public List<LineItemDto> LineItems { get; set; } = new();
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public bool ReceiptPresent { get; set; }
    public string Date { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? Scenario { get; set; }
}

public class LineItemDto
{
    public string Description { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
