using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Workflow.Services.Agent;

/// <summary>
/// Deterministic stub for CI/tests. Returns plausible agent decisions based on invoice properties.
/// The deterministic router still makes the final routing decision.
/// </summary>
public class StubLlmClient : ILlmClient
{
    public Task<AgentDecision> AnalyzeInvoiceAsync(InvoiceDto invoice, string policyText, CancellationToken ct = default)
    {
        var decision = AnalyzeSync(invoice);
        return Task.FromResult(decision);
    }

    private static AgentDecision AnalyzeSync(InvoiceDto invoice)
    {
        var violations = new List<string>();
        var confidence = 0.95;
        var recommendation = "approve";
        var reasons = new List<string>();

        // Unknown vendor
        if (!invoice.VendorKnown)
        {
            violations.Add("GLOBAL-VENDOR");
            recommendation = "escalate";
            confidence = Math.Min(confidence, 0.40);
            reasons.Add("Unknown vendor");
        }

        // Missing receipt
        if (!invoice.ReceiptPresent && invoice.Total > 25)
        {
            violations.Add("GLOBAL-RECEIPT");
            recommendation = "escalate";
            confidence = Math.Min(confidence, 0.50);
            reasons.Add("Missing receipt");
        }

        // Math mismatch
        if (invoice.LineItems.Count > 0)
        {
            var computed = invoice.LineItems.Sum(li => li.Quantity * li.UnitPrice) + invoice.TaxAmount;
            if (Math.Abs(computed - invoice.Total) > 0.01m)
            {
                violations.Add("GLOBAL-MATH");
                recommendation = "escalate";
                confidence = Math.Min(confidence, 0.30);
                reasons.Add("Math mismatch");
            }
        }

        // Alcohol-only
        if (invoice.Category == "meals" && invoice.LineItems.Any(li =>
            li.Description.Contains("alcohol", StringComparison.OrdinalIgnoreCase) ||
            li.Description.Contains("bar tab", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add("MEAL-03");
            recommendation = "reject";
            confidence = Math.Min(confidence, 0.90);
            reasons.Add("Alcohol-only receipt");
        }

        // Missing attendees for meals
        if (invoice.Category == "meals" && (!invoice.Attendees.HasValue || invoice.Attendees <= 0))
        {
            violations.Add("MEAL-01");
            recommendation = "escalate";
            confidence = Math.Min(confidence, 0.50);
            reasons.Add("Missing attendee count");
        }

        // Large amounts
        if (invoice.Total > 250)
        {
            confidence = Math.Min(confidence, 0.85);
            reasons.Add($"Amount ${invoice.Total} exceeds typical auto-approve range");
        }

        // Ambiguous/mixed category
        if (invoice.Category == "other")
        {
            confidence = Math.Min(confidence, 0.60);
            reasons.Add("Ambiguous category");
        }

        // Anti-steering: detect manipulation in notes
        if (!string.IsNullOrEmpty(invoice.Notes))
        {
            var lowerNotes = invoice.Notes.ToLowerInvariant();
            if (lowerNotes.Contains("approve me") || lowerNotes.Contains("no need to review") ||
                lowerNotes.Contains("already ok") || lowerNotes.Contains("skip review"))
            {
                reasons.Add("WARNING: Detected potential steering language in notes — ignoring");
            }
        }

        if (reasons.Count == 0)
            reasons.Add($"Standard {invoice.Category} expense, all checks pass");

        return new AgentDecision
        {
            Recommendation = recommendation,
            Confidence = confidence,
            Violations = violations,
            Reasoning = string.Join(". ", reasons) + "."
        };
    }
}
