using ApprovalFlow.Shared.Constants;
using ApprovalFlow.Shared.Models;

namespace ApprovalFlow.Workflow.Services;

public class RouterResult
{
    public RouteDecision Route { get; set; }
    public List<string> Violations { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public class DeterministicRouter
{
    private readonly PolicyConfig _config;
    private readonly FxRates _fxRates;

    public DeterministicRouter(PolicyConfig config, FxRates fxRates)
    {
        _config = config;
        _fxRates = fxRates;
    }

    public RouterResult Route(InvoiceDto invoice, AgentDecision agentDecision)
    {
        var violations = new List<string>();
        var usdAmount = _fxRates.ConvertToUsd(invoice.Total, invoice.Currency);

        // === 1. Hard stops — always human, regardless of agent ===

        // GLOBAL-VENDOR: unknown vendor
        if (!invoice.VendorKnown)
        {
            violations.Add(PolicyRules.GlobalVendor);
        }

        // GLOBAL-MATH: line items + tax must reconcile to total
        if (HasMathMismatch(invoice))
        {
            violations.Add(PolicyRules.GlobalMath);
        }

        // GLOBAL-FRAUD: suspicious patterns
        if (HasFraudSignals(invoice))
        {
            violations.Add(PolicyRules.GlobalFraud);
        }

        // GLOBAL-FX: foreign currency over $1000
        if (invoice.Currency != "USD" && usdAmount > _config.FxHardStopThreshold)
        {
            violations.Add(PolicyRules.GlobalFx);
        }

        // GLOBAL-RECEIPT: missing receipt for amount > $25
        if (!invoice.ReceiptPresent && invoice.Total > _config.ReceiptThreshold)
        {
            violations.Add(PolicyRules.GlobalReceipt);
        }

        // MEAL-01: missing attendee count for meals
        if (invoice.Category == Categories.Meals && (!invoice.Attendees.HasValue || invoice.Attendees.Value <= 0))
        {
            violations.Add(PolicyRules.Meal01_Attendees);
        }

        // MEAL-02: client entertainment over $500 without justification/client name
        if (invoice.Category == Categories.Meals && usdAmount > _config.ClientEntertainmentThreshold)
        {
            var hasClientInfo = !string.IsNullOrEmpty(invoice.Notes) &&
                                invoice.Notes.Contains("client", StringComparison.OrdinalIgnoreCase) &&
                                HasBusinessJustification(invoice);
            if (!hasClientInfo)
            {
                violations.Add(PolicyRules.Meal02_ClientEntertainment);
            }
        }

        // If any hard stop fired, return HUMAN_REVIEW
        if (violations.Count > 0)
        {
            // Check for reject-level violations first
            // MEAL-03: alcohol-only — reject, not just review
            if (IsAlcoholOnly(invoice))
            {
                violations.Add(PolicyRules.Meal03_AlcoholOnly);
                return new RouterResult
                {
                    Route = RouteDecision.Reject,
                    Violations = violations,
                    Reason = $"Rejected: alcohol-only receipt (MEAL-03). Additional violations: {string.Join(", ", violations)}"
                };
            }

            // Add autonomy ceiling check for completeness in violations list
            if (usdAmount > _config.AutonomyCeiling && !violations.Contains(PolicyRules.AutonomyCeiling))
                violations.Add(PolicyRules.AutonomyCeiling);

            return new RouterResult
            {
                Route = RouteDecision.HumanReview,
                Violations = violations,
                Reason = $"Escalated to human review: {string.Join(", ", violations)}"
            };
        }

        // === 2. Category-specific policy violations (no hard stop, but may escalate) ===

        // MEAL-03: alcohol-only receipts — REJECT
        if (IsAlcoholOnly(invoice))
        {
            violations.Add(PolicyRules.Meal03_AlcoholOnly);
            return new RouterResult
            {
                Route = RouteDecision.Reject,
                Violations = violations,
                Reason = "Rejected: alcohol-only receipts are not reimbursable (MEAL-03)."
            };
        }

        // SAAS-01: SaaS > $200/month
        if (invoice.Category == Categories.Saas && usdAmount > _config.SaasMonthlyCap)
        {
            violations.Add(PolicyRules.Saas01_MonthlyCap);
        }

        // HW-02: hardware > $1000 is capital
        if (invoice.Category == Categories.Hardware && usdAmount > _config.HardwareCapitalThreshold)
        {
            violations.Add(PolicyRules.Hw02_Capital);
        }

        // TRAVEL-02: single travel > $1500
        if (invoice.Category == Categories.Travel && usdAmount > _config.TravelSingleExpenseThreshold)
        {
            violations.Add(PolicyRules.Travel02_Over1500);
        }

        // TRAVEL-03: first/business class
        if (invoice.Category == Categories.Travel && IsFirstOrBusinessClass(invoice))
        {
            violations.Add(PolicyRules.Travel03_FirstBusiness);
        }

        if (violations.Count > 0)
        {
            if (usdAmount > _config.AutonomyCeiling)
                violations.Add(PolicyRules.AutonomyCeiling);

            return new RouterResult
            {
                Route = RouteDecision.HumanReview,
                Violations = violations,
                Reason = $"Escalated to human review: {string.Join(", ", violations)}"
            };
        }

        // === 3. Autonomy thresholds ===

        if (usdAmount > _config.AutonomyCeiling)
        {
            violations.Add(PolicyRules.AutonomyCeiling);
            return new RouterResult
            {
                Route = RouteDecision.HumanReview,
                Violations = violations,
                Reason = $"Amount ${usdAmount:F2} exceeds autonomy ceiling ${_config.AutonomyCeiling}."
            };
        }

        if (agentDecision.Confidence < _config.AutonomyConfidence)
        {
            violations.Add(PolicyRules.AutonomyConfidence);
            return new RouterResult
            {
                Route = RouteDecision.HumanReview,
                Violations = violations,
                Reason = $"Agent confidence {agentDecision.Confidence:P0} below threshold {_config.AutonomyConfidence:P0}."
            };
        }

        // === 4. All checks passed ===
        return new RouterResult
        {
            Route = RouteDecision.AutoApprove,
            Violations = new List<string>(),
            Reason = $"Auto-approved: amount ${usdAmount:F2} under ceiling, confidence {agentDecision.Confidence:P0}, all policy checks passed."
        };
    }

    private bool HasMathMismatch(InvoiceDto invoice)
    {
        if (invoice.LineItems.Count == 0) return false;
        var computed = invoice.LineItems.Sum(li => li.Quantity * li.UnitPrice) + invoice.TaxAmount;
        return Math.Abs(computed - invoice.Total) > 0.01m;
    }

    private bool HasFraudSignals(InvoiceDto invoice)
    {
        var signals = 0;

        // Round number (divisible by 1000 and > 1000)
        if (invoice.Total >= 1000 && invoice.Total % 1000 == 0)
            signals++;

        // New vendor
        if (!invoice.VendorKnown)
            signals++;

        // No line-item detail (single generic line)
        if (invoice.LineItems.Count == 1 && invoice.LineItems[0].Quantity == 1)
        {
            var desc = invoice.LineItems[0].Description.ToLowerInvariant();
            if (desc.Contains("consulting") || desc.Contains("services") || desc.Contains("professional"))
                signals++;
        }

        // Missing receipt
        if (!invoice.ReceiptPresent)
            signals++;

        // Need at least 2 signals to flag as fraud
        return signals >= 2;
    }

    private static bool IsAlcoholOnly(InvoiceDto invoice)
    {
        if (invoice.Category != Categories.Meals) return false;
        return invoice.LineItems.Any(li =>
        {
            var desc = li.Description.ToLowerInvariant();
            return desc.Contains("alcohol") || desc.Contains("bar tab") || desc.Contains("drinks only");
        }) && invoice.LineItems.All(li =>
        {
            var desc = li.Description.ToLowerInvariant();
            return desc.Contains("alcohol") || desc.Contains("bar tab") || desc.Contains("drinks only") ||
                   desc.Contains("wine") || desc.Contains("beer") || desc.Contains("cocktail");
        });
    }

    private static bool HasBusinessJustification(InvoiceDto invoice)
    {
        if (string.IsNullOrEmpty(invoice.Notes)) return false;
        var notes = invoice.Notes.ToLowerInvariant();
        return notes.Contains("justification") || notes.Contains("business reason") ||
               notes.Contains("purpose") || notes.Contains("regarding");
    }

    private static bool IsFirstOrBusinessClass(InvoiceDto invoice)
    {
        var allText = string.Join(" ", invoice.LineItems.Select(li => li.Description)) + " " + (invoice.Notes ?? "");
        var lower = allText.ToLowerInvariant();
        return lower.Contains("first class") || lower.Contains("business class") ||
               lower.Contains("first-class") || lower.Contains("business-class");
    }
}
