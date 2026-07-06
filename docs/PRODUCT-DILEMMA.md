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

From our 20 test invoices:

### Auto-Approved (4)
| Invoice | Amount | Category | Trigger/Reason |
|---------|--------|----------|----------------|
| INV-1001 | $42 | meals | - |
| INV-1002 | $99 | saas | - |
| INV-1016 | $48 | travel | - |
| INV-1017 | $180 | hardware | - |

### Human Review Required (14)
| Invoice | Amount | Category | Trigger/Reason |
|---------|--------|----------|----------------|
| INV-1003 | $1820 | meals | MEAL-02, AUTONOMY-CEILING |
| INV-1004 | $1400 | hardware | HW-02, AUTONOMY-CEILING |
| INV-1005 | $120 | meals | GLOBAL-RECEIPT |
| INV-1006 | $3000 | hardware | GLOBAL-MATH |
| INV-1008 | $5000 | other | GLOBAL-FRAUD, GLOBAL-VENDOR, GLOBAL-RECEIPT, AUTONOMY-CEILING |
| INV-1009 | 1200 EUR (~$1296) | travel | GLOBAL-FX, AUTONOMY-CEILING |
| INV-1010 | $480 | other | AUTONOMY-CONFIDENCE, AUTONOMY-CEILING |
| INV-1011 | $80 | saas | GLOBAL-VENDOR |
| INV-1012 | $9500 | hardware | HW-02, AUTONOMY-CEILING |
| INV-1013 | $300 | saas | AUTONOMY-CEILING |
| INV-1014A | $600 | other | AUTONOMY-CEILING |
| INV-1014B | $600 | other | AUTONOMY-CEILING |
| INV-1018 | $220 | saas | SAAS-01 |
| INV-1019 | $1750 | travel | TRAVEL-02, AUTONOMY-CEILING |

### Duplicate (1)
| Invoice | Amount | Category | Trigger/Reason |
|---------|--------|----------|----------------|
| INV-1007 | $42 | meals | GLOBAL-DUP |

### Rejected (1)
| Invoice | Amount | Category | Trigger/Reason |
|---------|--------|----------|----------------|
| INV-1015 | $60 | meals | MEAL-03 |

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
