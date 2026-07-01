# ADR-002: Service Decomposition

## Status
Accepted

## Context
The system needs to handle invoice intake, AI analysis, policy routing, payment processing, and human review. We need to decide how to split responsibilities across services.

## Decision
Four backend services + UI:
1. **Gateway** — YARP reverse proxy, rate limiting, correlation ID
2. **Invoice Service** — intake, validation, dedup, status queries
3. **Workflow Service** — AI agent, deterministic router, saga orchestration, HITL coordination
4. **Payment Service** — budget management, payment execution/reversal

## Consequences
- **Positive**: Clear separation of concerns; each service has a single responsibility
- **Positive**: Workflow Service owns all decision logic — easy to trace and audit
- **Negative**: More containers to manage (4 services + 4 Dapr sidecars + Redis + UI = 10 total)
- **Trade-off**: Dedup logic lives in Invoice Service (data concern), not Workflow (decision concern)
