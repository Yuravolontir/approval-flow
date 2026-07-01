# The Product Dilemma: Autonomy Posture

## The Question

How much should we let the AI approve on its own?

Every AI-assisted approval system faces a fundamental tension:
- **Too autonomous** → risk of approving fraudulent or policy-violating invoices
- **Too conservative** → human bottleneck, no value from automation

## Our Posture: Conservative but Practical

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| `AUTONOMY_CEILING` | $250 | Covers routine expenses; well below category hard stops |
| `AUTONOMY_CONFIDENCE` | 0.80 | Catches ambiguous cases while allowing clear-cut approvals |

### Why $250?

1. **Covers high-volume, low-risk expenses**: team meals ($42), small SaaS ($99), office supplies ($48), standard travel reimbursements ($180)
2. **Well below category thresholds**: client entertainment auto-caps at $500, hardware at $1000, travel at $1500
3. **Bounded blast radius**: worst case of a single false approval is $250 — manageable and auditable

### Why 80% confidence?

- At 0.80, the AI must be reasonably certain. Ambiguous cases (mixed categories, unusual vendors, borderline amounts) will have lower confidence and get escalated
- This is a floor, not a target — most clear-cut approvals will score 0.90+

## Hard Stops (Non-Negotiable)

These rules **always** force human review, regardless of amount or confidence:

| Rule | Trigger | Why |
|------|---------|-----|
| GLOBAL-VENDOR | Unknown vendor | Could be fraud |
| GLOBAL-MATH | Line items don't sum to total | Data integrity |
| GLOBAL-FRAUD | Suspicious patterns (round numbers, duplicate sequences) | Risk management |
| GLOBAL-FX | Foreign currency > $1000 USD equivalent | FX volatility |
| GLOBAL-RECEIPT | Missing receipt for amount > $25 | Compliance |
| MEAL-01 | Missing attendee list | Tax requirement |
| MEAL-02 | Client entertainment without justification | Audit trail |

## Evidence from Test Fixtures

From our 19 test invoices:

### Auto-Approved (no human needed)
| Invoice | Amount | Category | Why auto-approved |
|---------|--------|----------|-------------------|
| INV-1001 | $42.50 | meal | Below ceiling, known vendor, all info present |
| INV-1002 | $99.00 | saas | Monthly SaaS < $200, known vendor |
| INV-1016 | $48.00 | meal | Simple team meal, all rules pass |
| INV-1017 | $180.00 | travel | Standard domestic travel, below ceiling |

### Human Review Required
| Invoice | Amount | Trigger |
|---------|--------|---------|
| INV-1003 | $475.00 | Above $250 ceiling |
| INV-1004 | $1,200.00 | Above $250 + HW-02 (>$1000 capital) |
| INV-1005 | $89.00 | Missing attendee list (MEAL-01) |
| INV-1006 | $350.00 | Client entertainment without justification (MEAL-02) |
| INV-1008 | $12,500.00 | Way above ceiling |
| INV-1009 | $67.00 | Unknown vendor (GLOBAL-VENDOR) |
| INV-1010 | $234.00 | Mixed category → low confidence |

### Rejected
| Invoice | Trigger |
|---------|---------|
| INV-1011 | Alcohol-only meal (MEAL-03) |

## The Architecture Guarantee (M12)

The deterministic router is **provably incapable** of auto-approving above the ceiling:

```csharp
if (usdAmount > config.AutonomyCeiling)
    return RouteDecision.HumanReview;
```

This is a simple if-statement in pure C#. The AI agent has no way to bypass it — the agent's output is just one input to the router. The router makes the final decision.

## Configurability (M13)

Both thresholds are environment variables — changeable without redeployment:

```env
AUTONOMY_CEILING=250
AUTONOMY_CONFIDENCE=0.80
```

An organization could:
- Lower ceiling to $100 for stricter control during onboarding
- Raise ceiling to $500 after building trust in the AI's accuracy
- Raise confidence to 0.90 for regulated industries
- Lower confidence to 0.70 for high-throughput, low-risk environments

The system behavior changes immediately without code changes.
